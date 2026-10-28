using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    private const int TelnyxDecisionLimit = 64;
    private const string TelnyxDecisionEndpoint = "ai/typesafe/v1/systemone";
    private static readonly JsonSerializerOptions TelnyxDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var model = GetTelnyxDecisionModel(request.Model);
        ValidateTelnyxDecisionOptions(request.ProviderOptions);
        ValidateTelnyxDecisionState(request.State.Value);
        var questions = MapTelnyxDecisionQuestions(request.Questions);
        var state = JsonNode.Parse(request.State.Value.GetRawText())
            ?? throw new ArgumentException("Telnyx decision state cannot be null.", nameof(request));

        using var message = new HttpRequestMessage(HttpMethod.Post, TelnyxDecisionEndpoint)
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["state"] = state,
                ["questions"] = questions
            }, options: TelnyxDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Telnyx decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapTelnyxDecisionResponse(body, request, model);
            var headers = response.GetHeaders();
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = model,
                Timestamp = DateTimeOffset.UtcNow,
                Id = headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Telnyx returned an invalid decision response.", exception);
        }
    }

    private static void ValidateTelnyxDecisionState(JsonElement state)
    {
        if (ContainsTelnyxUnsupportedMedia(state))
            throw new NotSupportedException("Telnyx Decisions supports text-only inputs, not images or audio.");
    }

    private static bool ContainsTelnyxUnsupportedMedia(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() is "input_image" or "input_audio")
                return true;
            return value.EnumerateObject().Any(property => ContainsTelnyxUnsupportedMedia(property.Value));
        }
        return value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().Any(ContainsTelnyxUnsupportedMedia);
    }

    private async Task<OpenAIDecisionResponse> ExecuteOpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Telnyx Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("Telnyx Decisions supports text-only inputs, not images.");

        var model = GetTelnyxDecisionModel(request.Model);
        var state = request.Input.Text is not null
            ? new DecisionInput(request.Input.Text)
            : new DecisionInput(JsonSerializer.SerializeToElement(new { messages = request.Input }, TelnyxDecisionJsonOptions));
        var typedRequest = new DecisionRequest { Model = model, State = state };

        if (request.Questions.Count > TelnyxDecisionLimit)
            throw new ArgumentException("Telnyx Decisions supports at most 64 questions.", nameof(request));

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            typedRequest.Questions[TelnyxDecisionQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice when choice.Choices.Count is >= 2 and <= TelnyxDecisionLimit
                    => new DecisionChoiceQuestion
                    {
                        Instructions = question.Instructions,
                        Criteria = choice.Choices.Select((option, optionIndex) =>
                            KeyValuePair.Create(TelnyxDecisionOptionId(optionIndex),
                                (DecisionInput?)new DecisionInput(DescribeTelnyxChoice(option.Value, option.Description))))
                            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                    },
                OpenAIDecisionChoiceQuestion => throw new ArgumentException(
                    "Telnyx choice questions require 2 to 64 options.", nameof(request)),
                OpenAIDecisionScoreQuestion score when score.Levels.Count is >= 2 and <= TelnyxDecisionLimit
                    => new DecisionScoreQuestion
                    {
                        Instructions = question.Instructions,
                        Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                            DescribeTelnyxLevel(level.Label, level.Description))).ToArray()
                    },
                OpenAIDecisionScoreQuestion => throw new ArgumentException(
                    "Telnyx score questions require 2 to 64 levels.", nameof(request)),
                _ => throw new NotSupportedException("Telnyx Decisions does not support this OpenAI decision question.")
            };
        }

        var typedResponse = await ExecuteDecisionRequestAsync(typedRequest, cancellationToken);
        var body = typedResponse.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = TelnyxDecisionQuestionId(index);
            var question = request.Questions[index];
            var native = body.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, typedResponse.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean)
                    => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected)
                    => new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[int.Parse(selected.Choice[TelnyxDecisionOptionPrefix.Length..], CultureInfo.InvariantCulture)].Value,
                        Confidence = native.GetProperty("confidence").GetDouble(),
                        Probabilities = choice.Choices.Select((option, optionIndex) => new OpenAIDecisionChoiceProbability
                        {
                            Value = option.Value,
                            Probability = selected.Probabilities![TelnyxDecisionOptionId(optionIndex)]
                        }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored)
                    => new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / (score.Levels.Count - 1d),
                        Confidence = native.GetProperty("confidence").GetDouble(),
                        Probabilities = score.Levels.Select((level, levelIndex) => new OpenAIDecisionScoreProbability
                        {
                            Label = level.Label,
                            Value = levelIndex / (score.Levels.Count - 1d),
                            Probability = scored.Probabilities![TelnyxDecisionIndex(levelIndex)]
                        }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected Telnyx decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var usage = typedResponse.Usage!;
        var inputTokens = usage.InputTokens!.Value;
        var outputTokens = usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = model,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(typedResponse.ProviderMetadata, TelnyxDecisionJsonOptions)
            }
        };
    }

    private static string GetTelnyxDecisionModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model.ToLowerInvariant() switch
        {
            "telnyx/decision-flash" => "telnyx/decision-flash",
            "telnyx/decision-pro" => "telnyx/decision-pro",
            _ => throw new NotSupportedException(
                "Telnyx Decisions supports only telnyx/decision-flash and telnyx/decision-pro.")
        };
    }

    private static void ValidateTelnyxDecisionOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("telnyx", out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.telnyx must be an object.", nameof(providerOptions));
            if (options.EnumerateObject().Any())
                throw new NotSupportedException("Telnyx Decisions does not support provider options.");
        }
    }

    private static JsonObject MapTelnyxDecisionQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        if (questions.Count is < 1 or > TelnyxDecisionLimit)
            throw new ArgumentException("Telnyx Decisions supports 1 to 64 questions.", nameof(questions));

        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Telnyx decision question IDs must be nonempty.", nameof(questions));

            var mapped = new JsonObject
            {
                ["type"] = question switch
                {
                    DecisionBooleanQuestion => "noul",
                    DecisionChoiceQuestion => "choice",
                    DecisionScoreQuestion => "score",
                    _ => throw new NotSupportedException($"Telnyx Decisions does not support question '{id}' of type '{question.Type}'.")
                },
                ["instructions"] = DecisionInputNode(question.Instructions!)
            };

            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    var booleanCriteria = new JsonObject();
                    AddTelnyxBooleanCriterion(booleanCriteria, "true", boolean.Criteria.True);
                    AddTelnyxBooleanCriterion(booleanCriteria, "false", boolean.Criteria.False);
                    mapped["criteria"] = booleanCriteria;
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria is null || choice.Criteria.Count is < 2 or > TelnyxDecisionLimit)
                        throw new ArgumentException($"Telnyx choice question '{id}' requires 2 to 64 options.", nameof(questions));
                    var choiceCriteria = new JsonObject();
                    foreach (var (key, description) in choice.Criteria)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            throw new ArgumentException($"Telnyx choice question '{id}' has an empty option key.", nameof(questions));
                        if (description is not null && description.Value.ValueKind != JsonValueKind.String)
                            throw new NotSupportedException($"Telnyx choice question '{id}' descriptions must be strings or null.");
                        choiceCriteria[key] = description is null ? null : DecisionInputNode(description);
                    }
                    mapped["criteria"] = choiceCriteria;
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria is null || score.Criteria.Count is < 2 or > TelnyxDecisionLimit)
                        throw new ArgumentException($"Telnyx score question '{id}' requires 2 to 64 levels.", nameof(questions));
                    var scoreCriteria = new JsonArray();
                    foreach (var level in score.Criteria)
                    {
                        if (level is null || level.Value.ValueKind != JsonValueKind.String)
                            throw new NotSupportedException($"Telnyx score question '{id}' levels must be strings.");
                        scoreCriteria.Add(DecisionInputNode(level));
                    }
                    mapped["criteria"] = scoreCriteria;
                    break;
            }
            result[id] = mapped;
        }
        return result;
    }

    private static void AddTelnyxBooleanCriterion(JsonObject criteria, string name, DecisionInput? value)
    {
        if (value is null)
            return;
        if (value.Value.ValueKind != JsonValueKind.String)
            throw new NotSupportedException("Telnyx noul criteria descriptions must be strings.");
        criteria[name] = DecisionInputNode(value);
    }

    private static JsonNode? DecisionInputNode(DecisionInput input)
        => JsonNode.Parse(input.Value.GetRawText());

    private static DecisionResponse MapTelnyxDecisionResponse(
        JsonElement body, DecisionRequest request, string requestedModel)
    {
        if (body.GetProperty("model").GetString() != requestedModel)
            throw new JsonException("Telnyx returned a different decision model than requested.");
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = nativeAnswers.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (answer.GetProperty("type").GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadTelnyxProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    _ = ReadTelnyxProbability(answer.GetProperty("confidence"));
                    var choiceProbabilities = ReadTelnyxDistribution(
                        answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = answer.GetProperty("choice").GetString()
                        ?? throw new JsonException("Missing selected choice.");
                    if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException("Selected choice must have a maximal probability.");
                    answers[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = choiceProbabilities
                    };
                    break;
                case DecisionScoreQuestion score:
                    ReadTelnyxProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(TelnyxDecisionIndex).ToArray();
                    var scoreProbabilities = ReadTelnyxDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String))
                        throw new JsonException("Invalid score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expectedScore = scoreProbabilities.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1
                        || Math.Abs(value - expectedScore) > 1e-6)
                        throw new JsonException("Invalid probability-weighted score.");
                    answers[id] = new DecisionScoreAnswer
                    {
                        Score = value,
                        Probabilities = scoreProbabilities
                    };
                    break;
                default:
                    throw new JsonException("Unsupported decision question type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        if (inputTokens < 0 || outputTokens < 0)
            throw new JsonException("Invalid token usage.");
        return new()
        {
            Answers = answers,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }

    private static double ReadTelnyxProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadTelnyxDistribution(
        JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadTelnyxProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized probability distribution.");
        return result;
    }

    private const string TelnyxDecisionOptionPrefix = "option-";
    private static string TelnyxDecisionQuestionId(int index) => "question-" + TelnyxDecisionIndex(index);
    private static string TelnyxDecisionOptionId(int index) => TelnyxDecisionOptionPrefix + TelnyxDecisionIndex(index);
    private static string TelnyxDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static string DescribeTelnyxChoice(OpenAIDecisionChoiceValue value, string? description)
    {
        var label = value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString()!
            : value.Value.GetRawText();
        return string.IsNullOrWhiteSpace(description) ? label : $"{label}: {description}";
    }

    private static string DescribeTelnyxLevel(string label, string? description)
        => string.IsNullOrWhiteSpace(description) ? label : $"{label}: {description}";
}
