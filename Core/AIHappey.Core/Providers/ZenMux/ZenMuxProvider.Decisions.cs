using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZenMux;

public partial class ZenMuxProvider
{
    private static readonly JsonSerializerOptions ZenMuxDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var model = RemoveZenMuxModelPrefix(request.Model);
        ValidateZenMuxDecisionOptions(request.ProviderOptions);
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
            questions[id] = MapZenMuxDecisionQuestion(question, id);

        var state = JsonNode.Parse(request.State.Value.GetRawText())
            ?? throw new ArgumentException("ZenMux decision state cannot be null.", nameof(request));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["state"] = state,
                ["questions"] = questions
            }, options: ZenMuxDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ZenMux decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement.Clone();
            var result = MapZenMuxDecisionResponse(body, request);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body);
            var headers = response.GetHeaders();
            result.Response = new()
            {
                ModelId = ReadZenMuxString(body, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = ReadZenMuxString(body, "request_id")
                    ?? headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
                Body = body
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("ZenMux returned an invalid decision response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("ZenMux Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("ZenMux System One does not document OpenAI image decision inputs.");

        var native = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(request.Input, ZenMuxDecisionJsonOptions))
        };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            native.Questions[ZenMuxDecisionQuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion
                {
                    Instructions = question.Instructions
                },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, optionIndex) =>
                        KeyValuePair.Create(ZenMuxDecisionOptionId(optionIndex),
                            (DecisionInput?)new DecisionInput(DescribeZenMuxDecisionLevel(
                                option.Value.Value.GetRawText(), option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeZenMuxDecisionLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported OpenAI decision question type.", nameof(request))
            };
        }

        var response = await DecisionRequestAsync(native, cancellationToken);
        var body = response.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = ZenMuxDecisionQuestionId(index);
            var question = request.Questions[index];
            var original = body.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, response.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[ParseZenMuxOptionIndex(selected.Choice)].Value,
                        Confidence = ReadZenMuxProbability(original.GetProperty("confidence")),
                        Probabilities = choice.Choices.Select((option, optionIndex) =>
                            new OpenAIDecisionChoiceProbability
                            {
                                Value = option.Value,
                                Probability = selected.Probabilities![ZenMuxDecisionOptionId(optionIndex)]
                            }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                    new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / (score.Levels.Count - 1d),
                        Confidence = ReadZenMuxProbability(original.GetProperty("confidence")),
                        Probabilities = score.Levels.Select((level, levelIndex) =>
                            new OpenAIDecisionScoreProbability
                            {
                                Label = level.Label,
                                Value = levelIndex / (score.Levels.Count - 1d),
                                Probability = scored.Probabilities![ZenMuxDecisionIndex(levelIndex)]
                            }).ToArray()
                    },
                _ => throw new InvalidOperationException("ZenMux returned an unexpected decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = response.Usage?.InputTokens ?? 0;
        var outputTokens = response.Usage?.OutputTokens ?? 0;
        return new OpenAIDecisionResponse
        {
            Model = ReadZenMuxString(body, "model") ?? request.Model,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(response.ProviderMetadata, ZenMuxDecisionJsonOptions)
            }
        };
    }

    private static JsonObject MapZenMuxDecisionQuestion(DecisionQuestion question, string id)
    {
        var instructions = question.Instructions
            ?? throw new ArgumentException($"ZenMux decision question '{id}' requires instructions.");
        var result = new JsonObject
        {
            ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
            ["instructions"] = DecisionValue(instructions)
        };

        switch (question)
        {
            case DecisionBooleanQuestion { Criteria: not null } boolean:
                var criteria = new JsonObject();
                if (boolean.Criteria.True is not null)
                    criteria["true"] = DecisionValue(boolean.Criteria.True);
                if (boolean.Criteria.False is not null)
                    criteria["false"] = DecisionValue(boolean.Criteria.False);
                if (criteria.Count > 0)
                    result["criteria"] = criteria;
                break;
            case DecisionChoiceQuestion choice when choice.Criteria.Count > 0:
                var choices = new JsonObject();
                foreach (var (name, description) in choice.Criteria)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException($"ZenMux choice question '{id}' requires nonempty option names.");
                    choices[name] = description is null ? "" : DecisionValue(description);
                }
                result["criteria"] = choices;
                break;
            case DecisionScoreQuestion score when score.Criteria.Count >= 2 && score.Criteria.All(level => level is not null):
                result["criteria"] = new JsonArray(score.Criteria
                    .Select(level => (JsonNode?)DecisionValue(level!)).ToArray());
                break;
            case DecisionBooleanQuestion:
                break;
            default:
                throw new NotSupportedException($"ZenMux System One does not support question '{id}' with the supplied criteria.");
        }
        return result;
    }

    private static DecisionResponse MapZenMuxDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(ReadZenMuxString(body, "model")))
            throw new JsonException("Missing decision model.");
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question.");

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
                        Probability = ReadZenMuxProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadZenMuxProbability(answer.GetProperty("confidence"));
                    var choiceProbabilities = ReadZenMuxDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException("Choice must be a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = choiceProbabilities };
                    break;
                case DecisionScoreQuestion score:
                    ReadZenMuxProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(ZenMuxDecisionIndex).ToArray();
                    var scoreProbabilities = ReadZenMuxDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var level)
                            || !JsonElement.DeepEquals(level, score.Criteria[int.Parse(key, CultureInfo.InvariantCulture)]!.Value)))
                        throw new JsonException("Score legend must match the submitted rubric.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = scoreProbabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6)
                        throw new JsonException("Invalid weighted score.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = scoreProbabilities };
                    break;
                default:
                    throw new JsonException("Unsupported decision question type.");
            }
        }

        DecisionUsage? usageResult = null;
        if (body.TryGetProperty("usage", out var usage))
        {
            if (usage.ValueKind != JsonValueKind.Object)
                throw new JsonException("Invalid decision usage.");
            usageResult = new DecisionUsage
            {
                InputTokens = ReadZenMuxOptionalInt(usage, "input_tokens"),
                OutputTokens = ReadZenMuxOptionalInt(usage, "output_tokens")
            };
        }
        return new DecisionResponse { Answers = answers, Usage = usageResult };
    }

    private static void ValidateZenMuxDecisionOptions(Dictionary<string, JsonElement>? options)
    {
        if (options?.TryGetValue("zenmux", out var value) != true)
            return;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any())
            throw new NotSupportedException("ZenMux System One does not document provider options.");
    }

    private static JsonNode? DecisionValue(DecisionInput input)
        => JsonNode.Parse(input.Value.GetRawText());

    private static string RemoveZenMuxModelPrefix(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var native = model.StartsWith("zenmux/", StringComparison.OrdinalIgnoreCase)
            ? model["zenmux/".Length..]
            : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(native);
        return native;
    }

    private static Dictionary<string, double> ReadZenMuxDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadZenMuxProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized probability distribution.");
        return result;
    }

    private static double ReadZenMuxProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0,1].");
        return number;
    }

    private static int? ReadZenMuxOptionalInt(JsonElement value, string name)
        => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var number) ? number : null;

    private static string? ReadZenMuxString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static string ZenMuxDecisionQuestionId(int index) => "question-" + ZenMuxDecisionIndex(index);
    private static string ZenMuxDecisionOptionId(int index) => "option-" + ZenMuxDecisionIndex(index);
    private static string ZenMuxDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static int ParseZenMuxOptionIndex(string option)
    {
        const string prefix = "option-";
        if (!option.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(option[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0)
            throw new InvalidOperationException("ZenMux returned an invalid option identifier.");
        return index;
    }

    private static string DescribeZenMuxDecisionLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
