using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Featherless;

public partial class FeatherlessProvider
{
    private static readonly JsonSerializerOptions FeatherlessDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<DecisionResponse> DecisionRequestInternalAsync(
        DecisionRequest request,
        CancellationToken cancellationToken,
        bool useMessages = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier(), requiresInstructions: false);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new NotSupportedException("Featherless Decisions provider options are not supported.");

        var nativeModel = RemoveFeatherlessPrefix(request.Model);
        var body = new JsonObject
        {
            ["model"] = nativeModel,
            ["questions"] = MapFeatherlessQuestions(request.Questions)
        };
        body[useMessages ? "messages" : "state"] = JsonNode.Parse(request.State.Value.GetRawText());
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/classifier")
        {
            Content = JsonContent.Create(body, options: FeatherlessDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Featherless Decisions failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var responseBody = document.RootElement;
            var result = MapFeatherlessDecisionResponse(responseBody, request);
            var headers = response.GetHeaders();
            headers.TryGetValue("x-request-id", out var requestId);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(responseBody.Clone());
            result.Response = new()
            {
                ModelId = RequiredString(responseBody, "model").ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = requestId,
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value),
                Body = responseBody.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Featherless returned an invalid Decisions response.", exception);
        }
    }

    private async Task<OpenAIDecisionResponse> OpenAIDecisionRequestInternalAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Featherless Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("Featherless classifier messages do not document image input support.");

        var nativeRequest = new DecisionRequest
        {
            Model = request.Model,
            State = request.Input.Text is not null
                ? new DecisionInput(request.Input.Text)
                : new DecisionInput(JsonSerializer.SerializeToElement(
                    request.Input.Messages, FeatherlessDecisionJsonOptions))
        };

        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            nativeRequest.Questions[QuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion
                {
                    Instructions = new DecisionInput(question.Instructions)
                },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = new DecisionInput(question.Instructions),
                    Criteria = choice.Choices.Select((option, optionIndex) =>
                            KeyValuePair.Create(OptionId(optionIndex),
                                (DecisionInput?)new DecisionInput(DescribeLevel(
                                    option.Value.Value.ValueKind == JsonValueKind.String
                                        ? option.Value.Value.GetString()!
                                        : option.Value.Value.GetRawText(),
                                    option.Description))))
                        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Instructions = new DecisionInput(question.Instructions),
                    Criteria = score.Levels.Select(level =>
                        (DecisionInput?)new DecisionInput(DescribeLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported Featherless decision question type.", nameof(request))
            };
        }

        var typedResponse = await DecisionRequestInternalAsync(
            nativeRequest,
            cancellationToken,
            useMessages: request.Input.Messages is not null);
        var responseBody = (JsonElement)typedResponse.Response!.Body!;
        var nativeAnswers = responseBody.GetProperty("answers");
        var answers = new List<OpenAIDecisionAnswer>(request.Questions.Count);
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = QuestionId(index);
            var question = request.Questions[index];
            var rawAnswer = nativeAnswers.GetProperty(id);
            OpenAIDecisionAnswer answer = (question, typedResponse.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[ParseOptionIndex(selected.Choice)].Value,
                        Confidence = ReadProbability(rawAnswer.GetProperty("confidence")),
                        Probabilities = choice.Choices.Select((option, optionIndex) =>
                            new OpenAIDecisionChoiceProbability
                            {
                                Value = option.Value,
                                Probability = selected.Probabilities![OptionId(optionIndex)]
                            }).ToArray()
                    },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer scored) =>
                    new OpenAIDecisionScoreAnswer
                    {
                        Score = scored.Score / (score.Levels.Count - 1d),
                        Confidence = ReadProbability(rawAnswer.GetProperty("confidence")),
                        Probabilities = score.Levels.Select((level, levelIndex) =>
                            new OpenAIDecisionScoreProbability
                            {
                                Label = level.Label,
                                Value = levelIndex / (score.Levels.Count - 1d),
                                Probability = scored.Probabilities![Index(levelIndex)]
                            }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected Featherless decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = typedResponse.Usage!.InputTokens!.Value;
        var outputTokens = typedResponse.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = RequiredString(responseBody, "model"),
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(typedResponse.ProviderMetadata, FeatherlessDecisionJsonOptions)
            }
        };
    }

    private static JsonObject MapFeatherlessQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id == "__proto__" || question is null)
                throw new ArgumentException("Featherless decision question IDs must contain 1 to 128 characters.", nameof(questions));

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type
            };
            if (question.Instructions is not null)
                mapped["instructions"] = DecisionText(question.Instructions);

            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    var booleanCriteria = new JsonObject();
                    if (boolean.Criteria.True is not null)
                        booleanCriteria["true"] = DecisionText(boolean.Criteria.True);
                    if (boolean.Criteria.False is not null)
                        booleanCriteria["false"] = DecisionText(boolean.Criteria.False);
                    mapped["criteria"] = booleanCriteria;
                    break;
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: >= 2 and <= 50 }:
                    var choiceCriteria = new JsonObject();
                    foreach (var (key, value) in choice.Criteria)
                        choiceCriteria[key] = value is null ? key : DecisionText(value);
                    mapped["criteria"] = choiceCriteria;
                    break;
                case DecisionScoreQuestion score when score.Criteria is { Count: >= 2 and <= 50 }:
                    mapped["criteria"] = new JsonArray(score.Criteria
                        .Select((level, index) => (JsonNode?)JsonValue.Create(level is null
                            ? Index(index)
                            : DecisionText(level)))
                        .ToArray());
                    break;
                default:
                    throw new ArgumentException(
                        $"Featherless decision question '{id}' has invalid criteria or an unsupported type.",
                        nameof(questions));
            }
            result[id] = mapped;
        }
        return result;
    }

    private DecisionResponse MapFeatherlessDecisionResponse(JsonElement body, DecisionRequest request)
    {
        _ = RequiredString(body, "model");
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Any(item => item.Name == "__proto__"))
            throw new JsonException("Featherless classifier answers must cover every question exactly once.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = nativeAnswers.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (RequiredString(answer, "type") != expectedType)
                throw new JsonException("Featherless classifier answer type does not match the question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadProbability(answer.GetProperty("noul"), 0.01, 0.99)
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadProbability(answer.GetProperty("confidence"));
                    var selected = RequiredString(answer, "choice");
                    var choiceProbabilities = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException("Featherless selected choice must have a maximal probability.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = choiceProbabilities };
                    break;
                case DecisionScoreQuestion score:
                    ReadProbability(answer.GetProperty("confidence"));
                    var indices = Enumerable.Range(0, score.Criteria.Count).Select(Index).ToArray();
                    var scoreProbabilities = ReadDistribution(answer.GetProperty("probabilities"), indices);
                    var value = ReadFiniteNumber(answer.GetProperty("score"));
                    if (value < 0 || value > indices.Length - 1)
                        throw new JsonException("Featherless score is outside the configured level range.");
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != indices.Length
                        || indices.Any(index => !legend.TryGetProperty(index, out _)))
                        throw new JsonException("Featherless returned an invalid score legend.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = scoreProbabilities };
                    break;
                default:
                    throw new JsonException("Unsupported Featherless decision answer.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = ReadUsageCount(usage, "input_tokens");
        var outputTokens = ReadUsageCount(usage, "output_tokens");
        return new DecisionResponse
        {
            Answers = answers,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens },
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone())
        };
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != expected.Count)
            throw new JsonException("Featherless probability map must cover every option.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Featherless probability map contains an unknown or duplicate option.");
        if (Math.Abs(result.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Featherless probabilities must sum to one.");
        return result;
    }

    private static double ReadProbability(JsonElement value, double minimum = 0, double maximum = 1)
    {
        var probability = ReadFiniteNumber(value);
        if (probability < minimum || probability > maximum)
            throw new JsonException("Featherless probability is outside its documented range.");
        return probability;
    }

    private static double ReadFiniteNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Expected a finite Featherless classifier number.");
        return number;
    }

    private static int ReadUsageCount(JsonElement usage, string name)
    {
        var value = usage.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"Featherless usage '{name}' must be a nonnegative integer.");
        return count;
    }

    private static string RequiredString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString()!
                : throw new JsonException($"Featherless classifier response requires '{name}'.");

    private static string DecisionText(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString()! : input.Value.GetRawText();

    private string RemoveFeatherlessPrefix(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string QuestionId(int index) => "question-" + Index(index);
    private static string OptionId(int index) => "option-" + Index(index);
    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static int ParseOptionIndex(string option)
        => int.Parse(option["option-".Length..], CultureInfo.InvariantCulture);

    private static string DescribeLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;
}
