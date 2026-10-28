using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    private const string GreenPtDecisionModel = "metis";
    private const string GreenPtDecisionsEndpoint = "v1/systemone";
    private static readonly JsonSerializerOptions GreenPtDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);

        var model = RemoveProviderPrefix(request.Model);
        if (!string.Equals(model, GreenPtDecisionModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"GreenPT Decisions does not support model '{request.Model}'.");
        if (request.Questions is null || request.Questions.Count is < 1 or > 32)
            throw new ArgumentException("GreenPT Decisions requires 1 to 32 questions.", nameof(request));
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new NotSupportedException("GreenPT Decisions does not support provider options.");

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = MapDecisionQuestions(request.Questions)
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, GreenPtDecisionsEndpoint)
        {
            Content = JsonContent.Create(payload, options: GreenPtDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GreenPT Decisions failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapGreenPtDecisionResponse(body, request, model);
            var headers = response.GetHeaders();
            headers.TryGetValue("x-request-id", out var requestId);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = requestId,
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("GreenPT returned an invalid Decisions response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("GreenPT Decisions does not support safety_identifier or additional OpenAI request fields.");

        var state = request.Input.Text is not null
            ? new DecisionInput(request.Input.Text)
            : new DecisionInput(JsonSerializer.SerializeToElement(new { messages = request.Input }, GreenPtDecisionJsonOptions));
        var typedRequest = new DecisionRequest { Model = request.Model, State = state };
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            typedRequest.Questions[QuestionId(index)] = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion { Instructions = question.Instructions },
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    Instructions = question.Instructions,
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
                    Instructions = question.Instructions,
                    Criteria = score.Levels.Select(level =>
                        (DecisionInput?)new DecisionInput(DescribeLevel(level.Label, level.Description))).ToArray()
                },
                _ => throw new ArgumentException("Unsupported GreenPT decision question type.", nameof(request))
            };
        }

        var response = await DecisionRequestAsync(typedRequest, cancellationToken);
        var body = response.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = QuestionId(index);
            var question = request.Questions[index];
            var native = body.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, response.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[ParseOptionIndex(selected.Choice)].Value,
                        Confidence = ReadProbability(native.GetProperty("confidence")),
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
                        Confidence = ReadProbability(native.GetProperty("confidence")),
                        Probabilities = score.Levels.Select((level, levelIndex) =>
                            new OpenAIDecisionScoreProbability
                            {
                                Label = level.Label,
                                Value = levelIndex / (score.Levels.Count - 1d),
                                Probability = scored.Probabilities![Index(levelIndex)]
                            }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected GreenPT decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = response.Usage!.InputTokens!.Value;
        var outputTokens = response.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = body.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(response.ProviderMetadata, GreenPtDecisionJsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        type = "other",
                        message = "GreenPT reports aggregate input and output tokens only; OpenAI-compatible detail counters default to zero."
                    }
                }, GreenPtDecisionJsonOptions)
            }
        };
    }

    private static JsonObject MapDecisionQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id == "__proto__" || question is null)
                throw new ArgumentException("GreenPT decision question IDs must contain 1 to 128 characters.", nameof(questions));

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type
            };
            if (question.Instructions is { } instructions)
                mapped["instructions"] = JsonSerializer.SerializeToNode(instructions, GreenPtDecisionJsonOptions);

            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is { } criteria)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(criteria, GreenPtDecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: >= 2 and <= 255 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, GreenPtDecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is { Count: >= 2 and <= 10 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, GreenPtDecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException($"GreenPT decision question '{id}' has invalid criteria or an unsupported type.", nameof(questions));
            }
            result[id] = mapped;
        }
        return result;
    }

    private static DecisionResponse MapGreenPtDecisionResponse(
        JsonElement body,
        DecisionRequest request,
        string model)
    {
        if (body.ValueKind != JsonValueKind.Object || body.GetProperty("model").GetString() != model)
            throw new JsonException("Decision response model mismatch.");

        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Any(item => item.Name == "__proto__"))
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
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var choiceProbabilities = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                        || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException("Selected choice must be a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = choiceProbabilities };
                    break;
                case DecisionScoreQuestion score:
                    ReadProbability(answer.GetProperty("confidence"));
                    var indices = Enumerable.Range(0, score.Criteria.Count).Select(Index).ToArray();
                    var scoreProbabilities = ReadDistribution(answer.GetProperty("probabilities"), indices);
                    var value = ReadFiniteNumber(answer.GetProperty("score"));
                    if (value < 0 || value > indices.Length - 1
                        || Math.Abs(value - scoreProbabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value)) > 1e-6)
                        throw new JsonException("Invalid weighted score.");
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != indices.Length
                        || indices.Any(index => !legend.TryGetProperty(index, out _)))
                        throw new JsonException("Invalid score legend.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = scoreProbabilities };
                    break;
                default:
                    throw new JsonException("Unsupported decision answer type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        if (inputTokens < 0 || outputTokens < 0)
            throw new JsonException("Usage token counts cannot be negative.");
        return new DecisionResponse
        {
            Answers = answers,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens },
            Warnings = []
        };
    }

    private static double ReadProbability(JsonElement value)
    {
        var probability = ReadFiniteNumber(value);
        if (probability is < 0 or > 1)
            throw new JsonException("Probability must be in [0, 1].");
        return probability;
    }

    private static double ReadFiniteNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Expected a finite number.");
        return number;
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != expected.Count)
            throw new JsonException("Probability map must cover every option.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Probability map contains an unknown or duplicate option.");
        if (Math.Abs(result.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Probabilities must sum to one.");
        return result;
    }

    private string RemoveProviderPrefix(string model)
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
