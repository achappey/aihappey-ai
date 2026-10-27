using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private const string AlibabaDecisionEndpoint = "compatible-mode/v1/systemone";

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var model = GetAlibabaDecisionModel(request.Model);
        ValidateAlibabaDecisionOptions(request.ProviderOptions);
        var questions = MapAlibabaDecisionQuestions(request.Questions);
        var state = JsonNode.Parse(request.State.Value.GetRawText())
            ?? throw new ArgumentException("Alibaba decision state cannot be null.", nameof(request));
        var (body, headers) = await SendAlibabaDecisionAsync(
            model, state, questions, request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);

        try
        {
            var result = MapAlibabaDecisionResponse(body, request);
            var responseHeaders = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase);
            headers.TryGetValue("x-request-id", out var headerRequestId);
            var requestId = ReadAlibabaString(body, "request_id")
                ?? headerRequestId;
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = ReadAlibabaString(body, "model")!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = requestId,
                Headers = responseHeaders,
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Alibaba returned an invalid decision response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.Questions.Count > 16)
            throw new ArgumentException("Alibaba System One supports at most 16 questions.", nameof(request));
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Alibaba Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("Alibaba System One does not document support for image inputs.");

        var model = GetAlibabaDecisionModel(request.Model);
        var typedRequest = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(new { messages = request.Input }))
        };
        var questions = new JsonObject();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            var id = AlibabaDecisionQuestionId(index);
            typedRequest.Questions[id] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion
                {
                    Instructions = question.Instructions
                },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = choice.Choices.Select((option, optionIndex) =>
                        KeyValuePair.Create(AlibabaDecisionOptionId(optionIndex),
                            (DecisionInput?)new DecisionInput(DescribeAlibabaChoice(option.Value.Value, option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                        DescribeAlibabaLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported decision question type.", nameof(request))
            };
            questions[id] = MapAlibabaDecisionQuestion(typedRequest.Questions[id], id);
        }

        var state = JsonNode.Parse(typedRequest.State.Value.GetRawText())
            ?? throw new ArgumentException("Alibaba decision state cannot be null.", nameof(request));
        var (body, _) = await SendAlibabaDecisionAsync(model, state, questions, null, cancellationToken);

        try
        {
            var typedResponse = MapAlibabaDecisionResponse(body, typedRequest);
            var answers = new List<OpenAIDecisionAnswer>();
            for (var index = 0; index < request.Questions.Count; index++)
            {
                var question = request.Questions[index];
                var id = AlibabaDecisionQuestionId(index);
                var native = body.GetProperty("answers").GetProperty(id);
                OpenAIDecisionAnswer answer = (question, typedResponse.Answers[id]) switch
                {
                    (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                        new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                    (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                        new OpenAIDecisionChoiceAnswer
                        {
                            Choice = choice.Choices[ParseAlibabaOptionIndex(selected.Choice)].Value,
                            Confidence = ReadAlibabaProbability(native.GetProperty("confidence")),
                            Probabilities = choice.Choices.Select((option, optionIndex) =>
                                new OpenAIDecisionChoiceProbability
                                {
                                    Value = option.Value,
                                    Probability = selected.Probabilities![AlibabaDecisionOptionId(optionIndex)]
                                }).ToArray()
                        },
                    (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                        new OpenAIDecisionScoreAnswer
                        {
                            Score = scored.Score,
                            Confidence = ReadAlibabaProbability(native.GetProperty("confidence")),
                            Probabilities = score.Levels.Select((level, levelIndex) =>
                                new OpenAIDecisionScoreProbability
                                {
                                    Label = level.Label,
                                    Value = levelIndex,
                                    Probability = scored.Probabilities![AlibabaDecisionIndex(levelIndex)]
                                }).ToArray()
                        },
                    _ => throw new InvalidOperationException("Unexpected Alibaba decision answer type.")
                };
                answer.Name = question.Name;
                answers.Add(answer);
            }

            var inputTokens = typedResponse.Usage!.InputTokens!.Value;
            return new()
            {
                Model = ReadAlibabaString(body, "model")!,
                Answers = answers,
                Usage = new()
                {
                    InputTokens = inputTokens,
                    OutputTokens = 0,
                    TotalTokens = inputTokens
                },
                AdditionalProperties = new()
                {
                    ["providerMetadata"] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                    {
                        [GetIdentifier()] = body.Clone()
                    }),
                    ["warnings"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new { type = "other", message = "Alibaba reports input_tokens only; output_tokens is set to zero because System One does not generate text." }
                    })
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Alibaba returned an invalid decision response.", exception);
        }
    }

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SendAlibabaDecisionAsync(
        string model,
        JsonNode state,
        JsonObject questions,
        Dictionary<string, string>? passthroughHeaders,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AlibabaDecisionEndpoint)
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["model"] = model,
                ["state"] = state,
                ["questions"] = questions
            })
        };
        foreach (var (name, value) in passthroughHeaders ?? [])
            request.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Alibaba decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Alibaba returned an invalid decision response.", exception);
        }
    }

    private static string GetAlibabaDecisionModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prefix = "alibaba/";
        var nativeModel = model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeModel);
        return nativeModel;
    }

    private void ValidateAlibabaDecisionOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue(GetIdentifier(), out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.alibaba must be an object.");
        if (options.EnumerateObject().Any())
            throw new NotSupportedException("Alibaba System One does not support provider options.");
    }

    private static JsonObject MapAlibabaDecisionQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        if (questions.Count > 16)
            throw new ArgumentException("Alibaba System One supports at most 16 questions.");
        var mapped = new JsonObject();
        foreach (var (id, question) in questions)
            mapped[id] = MapAlibabaDecisionQuestion(question, id);
        return mapped;
    }

    private static JsonObject MapAlibabaDecisionQuestion(DecisionQuestion question, string id)
    {
        var instructions = question.Instructions
            ?? throw new ArgumentException($"Alibaba question '{id}' requires instructions.");
        var mapped = new JsonObject
        {
            ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
            ["instructions"] = RenderAlibabaDecisionText(instructions)
        };
        switch (question)
        {
            case DecisionBooleanQuestion { Criteria: not null } boolean:
                var criteria = new JsonObject();
                if (boolean.Criteria.True is not null)
                    criteria["true"] = RenderAlibabaDecisionText(boolean.Criteria.True);
                if (boolean.Criteria.False is not null)
                    criteria["false"] = RenderAlibabaDecisionText(boolean.Criteria.False);
                if (criteria.Count > 0)
                    mapped["criteria"] = criteria;
                break;
            case DecisionChoiceQuestion choice:
                if (choice.Criteria.Count is < 1 or > 255
                    || choice.Criteria.Keys.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException($"Alibaba choice question '{id}' requires 1 to 255 nonempty option names.");
                var choices = new JsonObject();
                foreach (var (name, description) in choice.Criteria)
                    choices[name] = description is null ? "" : RenderAlibabaDecisionText(description);
                mapped["criteria"] = choices;
                break;
            case DecisionScoreQuestion score:
                if (score.Criteria.Count is < 2 or > 255 || score.Criteria.Any(level => level is null))
                    throw new ArgumentException($"Alibaba score question '{id}' requires 2 to 255 level descriptions.");
                var levels = new JsonArray();
                foreach (var level in score.Criteria)
                    levels.Add(RenderAlibabaDecisionText(level!));
                mapped["criteria"] = levels;
                break;
            case DecisionBooleanQuestion:
                break;
            default:
                throw new NotSupportedException($"Alibaba System One does not support question type '{question.Type}'.");
        }
        return mapped;
    }

    private static string RenderAlibabaDecisionText(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String
            ? input.Value.GetString()!
            : input.Value.GetRawText();

    private static DecisionResponse MapAlibabaDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(ReadAlibabaString(body, "model")))
            throw new JsonException("Missing decision model.");
        var answers = body.GetProperty("answers");
        if (answers.ValueKind != JsonValueKind.Object
            || answers.EnumerateObject().Count() != request.Questions.Count
            || answers.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");

        var mapped = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = answers.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (answer.GetProperty("type").GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    mapped[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadAlibabaProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadAlibabaProbability(answer.GetProperty("confidence"));
                    var choiceDistribution = ReadAlibabaDistribution(
                        answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = answer.GetProperty("choice").GetString()
                        ?? throw new JsonException("Missing selected choice.");
                    if (!choiceDistribution.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceDistribution.Values.Max())
                        throw new JsonException("Selected choice must be a maximal option.");
                    mapped[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = choiceDistribution
                    };
                    break;
                case DecisionScoreQuestion score:
                    ReadAlibabaProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(AlibabaDecisionIndex).ToArray();
                    var scoreDistribution = ReadAlibabaDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object
                        || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String))
                        throw new JsonException("Invalid score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expectedScore = scoreDistribution.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1
                        || Math.Abs(value - expectedScore) > 1e-6)
                        throw new JsonException("Score must be the probability-weighted level position.");
                    mapped[id] = new DecisionScoreAnswer
                    {
                        Score = value,
                        Probabilities = scoreDistribution
                    };
                    break;
                default:
                    throw new JsonException("Unsupported decision question type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        if (inputTokens < 0)
            throw new JsonException("Invalid decision usage.");
        return new()
        {
            Answers = mapped,
            Usage = new() { InputTokens = inputTokens }
        };
    }

    private static Dictionary<string, double> ReadAlibabaDistribution(
        JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name)
                || !result.TryAdd(property.Name, ReadAlibabaProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized probability distribution.");
        return result;
    }

    private static double ReadAlibabaProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Expected a finite probability in [0,1].");
        return number;
    }

    private static string? ReadAlibabaString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string AlibabaDecisionQuestionId(int index)
        => "question-" + AlibabaDecisionIndex(index);

    private static string AlibabaDecisionOptionId(int index)
        => "option-" + AlibabaDecisionIndex(index);

    private static string AlibabaDecisionIndex(int index)
        => index.ToString(CultureInfo.InvariantCulture);

    private static int ParseAlibabaOptionIndex(string option)
    {
        const string prefix = "option-";
        if (!option.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(option[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0)
            throw new InvalidOperationException("Alibaba returned an invalid option identifier.");
        return index;
    }

    private static string DescribeAlibabaChoice(JsonElement value, string? description)
    {
        var label = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        return DescribeAlibabaLevel(label, description);
    }

    private static string DescribeAlibabaLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
