using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AnyRouter;

public partial class AnyRouterProvider
{
    private static readonly JsonSerializerOptions AnyRouterDecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<DecisionResponse> DecisionRequestInternal(
        DecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ValidateAnyRouterDecisionOptions(request.ProviderOptions);
        var questions = MapAnyRouterDecisionQuestions(request.Questions);
        var state = JsonNode.Parse(request.State.Value.GetRawText())
            ?? throw new ArgumentException("AnyRouter decision state cannot be null.", nameof(request));
        var (body, headers) = await SendAnyRouterDecisionAsync(
            request.Model, state, questions, request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);
        try
        {
            return new DecisionResponse
            {
                Answers = MapAnyRouterDecisionAnswers(body, request.Questions),
                Usage = ReadAnyRouterDecisionUsage(body),
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new()
                {
                    ModelId = ReadAnyRouterString(body, "model")!.ToModelId(GetIdentifier()),
                    Timestamp = DateTimeOffset.UtcNow,
                    Id = ReadAnyRouterString(body, "id") ?? ReadAnyRouterHeader(headers, "x-request-id"),
                    Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                    Body = body.Clone()
                },
                Warnings = []
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("AnyRouter returned an invalid Decisions response.", exception);
        }
    }

    private async Task<OpenAIDecisionResponse> OpenAIDecisionRequestInternal(
        OpenAIDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("AnyRouter Decisions does not support safety_identifier or additional request fields.");

        var typedRequest = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(request.Input.Messages, AnyRouterDecisionJsonOptions))
        };
        var questions = new JsonObject();
        var answerValues = new Dictionary<string, IReadOnlyList<OpenAIDecisionChoiceValue>>(StringComparer.Ordinal);

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var source = request.Questions[index];
            var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
            if (source is OpenAIDecisionChoiceQuestion choice)
            {
                typedRequest.Questions[id] = MapAnyRouterOpenAIChoiceQuestion(choice, out var values);
                answerValues[id] = values;
            }
            else
            {
                typedRequest.Questions[id] = source switch
                {
                    OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = source.Instructions },
                    OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                    {
                        Instructions = source.Instructions,
                        Criteria = score.Levels.Select(level => (DecisionInput?)new DecisionInput(
                            level.Description ?? level.Label)).ToArray()
                    },
                    _ => throw new NotSupportedException($"AnyRouter Decisions does not support question type '{source.Type}'.")
                };
            }
            questions[id] = MapAnyRouterDecisionQuestion(typedRequest.Questions[id], id);
        }

        var state = JsonNode.Parse(typedRequest.State.Value.GetRawText())
            ?? throw new ArgumentException("AnyRouter decision state cannot be null.", nameof(request));
        var (body, _) = await SendAnyRouterDecisionAsync(
            request.Model, state, questions, null, cancellationToken);
        try
        {
            var typedAnswers = MapAnyRouterDecisionAnswers(body, typedRequest.Questions);
            var usage = ReadAnyRouterDecisionUsage(body);
            var answers = new List<OpenAIDecisionAnswer>();

            for (var index = 0; index < request.Questions.Count; index++)
            {
                var source = request.Questions[index];
                var id = "question-" + index.ToString(CultureInfo.InvariantCulture);
                var native = body.GetProperty("answers").GetProperty(id);
                OpenAIDecisionAnswer answer = (source, typedAnswers[id]) switch
                {
                    (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                        new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                    (OpenAIDecisionChoiceQuestion, DecisionChoiceAnswer choice) =>
                        new OpenAIDecisionChoiceAnswer
                        {
                            Choice = answerValues[id][ParseAnyRouterChoiceIndex(choice.Choice, answerValues[id].Count)],
                            Confidence = ReadAnyRouterProbability(native.GetProperty("confidence")),
                            Probabilities = answerValues[id].Select((value, choiceIndex) =>
                                new OpenAIDecisionChoiceProbability
                                {
                                    Value = value,
                                    Probability = choice.Probabilities![AnyRouterChoiceKey(choiceIndex)]
                                }).ToArray()
                        },
                    (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                        new OpenAIDecisionScoreAnswer
                        {
                            Score = scored.Score,
                            Confidence = ReadAnyRouterProbability(native.GetProperty("confidence")),
                            Probabilities = score.Levels.Select((level, levelIndex) =>
                                new OpenAIDecisionScoreProbability
                                {
                                    Label = level.Label,
                                    Value = levelIndex,
                                    Probability = scored.Probabilities![AnyRouterScoreKey(levelIndex)]
                                }).ToArray()
                        },
                    _ => throw new InvalidOperationException("AnyRouter returned an unexpected decision answer type.")
                };
                answer.Name = source.Name;
                answers.Add(answer);
            }

            var inputTokens = usage?.InputTokens ?? 0;
            var outputTokens = usage?.OutputTokens ?? 0;
            return new OpenAIDecisionResponse
            {
                Model = ReadAnyRouterString(body, "model")!,
                Answers = answers,
                Usage = new()
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = checked(inputTokens + outputTokens)
                },
                AdditionalProperties = new()
                {
                    ["providerMetadata"] = JsonSerializer.SerializeToElement(
                        GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()))
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("AnyRouter returned an invalid Decisions response.", exception);
        }
    }

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SendAnyRouterDecisionAsync(
        string model,
        JsonNode state,
        JsonObject questions,
        Dictionary<string, string>? passthroughHeaders,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = GetAnyRouterNativeModel(model),
                state,
                questions
            }, AnyRouterDecisionJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var (name, value) in passthroughHeaders ?? [])
            request.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(raw)
                    ? $"AnyRouter Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"AnyRouter Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (ValidateAnyRouterDecisionResponse(document.RootElement, questions), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("AnyRouter returned an invalid Decisions response.", exception);
        }
    }

    private static JsonElement ValidateAnyRouterDecisionResponse(JsonElement body, JsonObject questions)
    {
        if (body.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(ReadAnyRouterString(body, "model"))
            || !body.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object
            || answers.EnumerateObject().Count() != questions.Count
            || !body.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("AnyRouter Decisions response requires model, answers, and usage.");
        _ = ReadAnyRouterDecisionUsage(body);
        foreach (var question in questions)
        {
            var expectedType = question.Value?["type"]?.GetValue<string>();
            if (!answers.TryGetProperty(question.Key, out var answer)
                || answer.ValueKind != JsonValueKind.Object
                || ReadAnyRouterString(answer, "type") != expectedType)
                throw new JsonException("AnyRouter decision answers must match every question ID and type.");
        }
        return body.Clone();
    }

    private static JsonObject MapAnyRouterDecisionQuestions(
        Dictionary<string, DecisionQuestion> questions)
    {
        var mapped = new JsonObject();
        foreach (var (id, question) in questions)
            mapped[id] = MapAnyRouterDecisionQuestion(question, id);
        return mapped;
    }

    private static JsonObject MapAnyRouterDecisionQuestion(DecisionQuestion question, string id)
    {
        var instructions = question.Instructions
            ?? throw new ArgumentException($"AnyRouter question '{id}' requires instructions.");
        var mapped = new JsonObject
        {
            ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
            ["instructions"] = RenderAnyRouterDecisionInput(instructions)
        };

        switch (question)
        {
            case DecisionBooleanQuestion { Criteria: not null } boolean:
                var booleanCriteria = new JsonObject();
                if (boolean.Criteria.True is not null)
                    booleanCriteria["true"] = RenderAnyRouterDecisionInput(boolean.Criteria.True);
                if (boolean.Criteria.False is not null)
                    booleanCriteria["false"] = RenderAnyRouterDecisionInput(boolean.Criteria.False);
                if (booleanCriteria.Count > 0)
                    mapped["criteria"] = booleanCriteria;
                break;
            case DecisionChoiceQuestion choice:
                if (choice.Criteria.Keys.Any(string.IsNullOrWhiteSpace))
                    throw new ArgumentException($"AnyRouter choice question '{id}' requires nonempty option names.");
                var criteria = new JsonObject();
                foreach (var (name, description) in choice.Criteria)
                    criteria[name] = description is null ? null : RenderAnyRouterDecisionInput(description);
                mapped["criteria"] = criteria;
                break;
            case DecisionScoreQuestion score:
                if (score.Criteria.Count is < 2 or > 10)
                    throw new ArgumentException($"AnyRouter score question '{id}' requires 2 to 10 ordered levels.");
                var levels = new JsonArray();
                foreach (var level in score.Criteria)
                    levels.Add(level is null
                        ? throw new NotSupportedException("AnyRouter score criteria must contain text descriptions.")
                        : RenderAnyRouterDecisionInput(level));
                mapped["criteria"] = levels;
                break;
            case DecisionBooleanQuestion:
                break;
            default:
                throw new NotSupportedException($"AnyRouter Decisions does not support question type '{question.Type}'.");
        }
        return mapped;
    }

    private static DecisionChoiceQuestion MapAnyRouterOpenAIChoiceQuestion(
        OpenAIDecisionChoiceQuestion question,
        out IReadOnlyList<OpenAIDecisionChoiceValue> values)
    {
        var mappedValues = new List<OpenAIDecisionChoiceValue>();
        var criteria = new Dictionary<string, DecisionInput?>(StringComparer.Ordinal);
        foreach (var (choice, index) in question.Choices.Select((choice, index) => (choice, index)))
        {
            var key = AnyRouterChoiceKey(index);
            mappedValues.Add(choice.Value);
            criteria[key] = choice.Description is null ? null : new DecisionInput(choice.Description);
        }
        values = mappedValues;
        return new DecisionChoiceQuestion
        {
            Instructions = question.Instructions,
            Criteria = criteria
        };
    }

    private static Dictionary<string, DecisionAnswer> MapAnyRouterDecisionAnswers(
        JsonElement body, Dictionary<string, DecisionQuestion> questions)
    {
        var answers = body.GetProperty("answers");
        var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in questions)
        {
            var answer = answers.GetProperty(id);
            switch (question)
            {
                case DecisionBooleanQuestion:
                    result[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadAnyRouterProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    _ = ReadAnyRouterProbability(answer.GetProperty("confidence"));
                    var choiceProbabilities = ReadAnyRouterDistribution(
                        answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var selected = ReadAnyRouterString(answer, "choice")
                        ?? throw new JsonException("AnyRouter decision answer is missing its choice.");
                    if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException("AnyRouter selected choice must have a maximal probability.");
                    result[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = choiceProbabilities
                    };
                    break;
                case DecisionScoreQuestion score:
                    _ = ReadAnyRouterProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(AnyRouterScoreKey).ToArray();
                    var scoreProbabilities = ReadAnyRouterDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object
                        || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label)
                            || label.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        throw new JsonException("AnyRouter returned an invalid score legend.");
                    if (!answer.TryGetProperty("score", out var scoreValue)
                        || !scoreValue.TryGetDouble(out var nativeScore))
                        throw new JsonException("AnyRouter returned an invalid score.");
                    if (!double.IsFinite(nativeScore) || nativeScore < 0 || nativeScore > score.Criteria.Count - 1)
                        throw new JsonException("AnyRouter returned a score outside the supplied levels.");
                    var expectedScore = scoreProbabilities.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (Math.Abs(nativeScore - expectedScore) > 0.001)
                        throw new JsonException("AnyRouter score does not match its probability-weighted level.");
                    result[id] = new DecisionScoreAnswer
                    {
                        Score = nativeScore,
                        Probabilities = scoreProbabilities
                    };
                    break;
                default:
                    throw new NotSupportedException($"AnyRouter Decisions does not support question type '{question.Type}'.");
            }
        }
        return result;
    }

    private static Dictionary<string, double> ReadAnyRouterDistribution(
        JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("AnyRouter decision answer requires a probability map.");
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name)
                || !probabilities.TryAdd(property.Name, ReadAnyRouterProbability(property.Value)))
                throw new JsonException("AnyRouter decision probability map contains an unknown or duplicate key.");
        if (probabilities.Count != expected.Count || Math.Abs(probabilities.Values.Sum() - 1) > 1e-6)
            throw new JsonException("AnyRouter decision probabilities must cover all options and sum to one.");
        return probabilities;
    }

    private static DecisionUsage? ReadAnyRouterDecisionUsage(JsonElement body)
    {
        if (!body.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("AnyRouter decision response requires usage.");
        var input = ReadAnyRouterOptionalTokenCount(usage, "input_tokens");
        var output = ReadAnyRouterOptionalTokenCount(usage, "output_tokens");
        if (input is null && output is null)
            throw new JsonException("AnyRouter decision usage requires input_tokens or output_tokens.");
        return new DecisionUsage { InputTokens = input, OutputTokens = output };
    }

    private static int? ReadAnyRouterOptionalTokenCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;
        if (!value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"AnyRouter usage.{propertyName} must be a nonnegative integer.");
        return count;
    }

    private static double ReadAnyRouterProbability(JsonElement value)
    {
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("AnyRouter decision probabilities and confidence must be finite values in [0,1].");
        return number;
    }

    private static void ValidateAnyRouterDecisionOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("anyrouter", out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.anyrouter must be an object.", nameof(providerOptions));
        if (options.EnumerateObject().Any())
            throw new NotSupportedException("AnyRouter Decisions does not document provider-specific options.");
    }

    private static string RenderAnyRouterDecisionInput(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();

    private static string GetAnyRouterNativeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        const string prefix = "anyrouter/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string AnyRouterChoiceKey(int index)
        => "option-" + index.ToString(CultureInfo.InvariantCulture);

    private static int ParseAnyRouterChoiceIndex(string key, int count)
    {
        const string prefix = "option-";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index < 0 || index >= count)
            throw new JsonException("AnyRouter returned an invalid option identifier.");
        return index;
    }

    private static string AnyRouterScoreKey(int index)
        => index.ToString(CultureInfo.InvariantCulture);

    private static string? ReadAnyRouterString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? ReadAnyRouterHeader(IDictionary<string, string> headers, string name)
        => headers.FirstOrDefault(header => header.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
