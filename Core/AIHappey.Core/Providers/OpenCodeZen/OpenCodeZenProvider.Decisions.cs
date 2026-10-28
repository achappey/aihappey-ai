using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenCodeZen;

public partial class OpenCodeZenProvider
{
    private const int MaxChoiceCount = 52;
    private const int MaxScoreCount = 10;
    private const string Endpoint = "/inference/systemone/v1/systemone";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.StateFormat == "sdk" && HasSdkImageFile(request.State.Value))
            throw new NotSupportedException("OpenCodeZen Decisions does not support SDK image files.");
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var model = RemoveProviderPrefix(request.Model, GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new NotSupportedException("OpenCodeZen Decisions does not support provider options.");

        var questions = MapQuestions(request.Questions);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = questions
        };
        var (body, headers) = await SendDecisionAsync(
            payload, request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()), cancellationToken);

        try
        {
            var result = MapResponse(body, request, model);
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
            throw new InvalidOperationException("OpenCodeZen returned an invalid decision response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("OpenCodeZen Decisions does not support safety_identifier or additional OpenAI request fields.");
        if (request.Input.Messages?.Any(message =>
            message.Content.Parts?.Any(part => part is OpenAIDecisionInputImage) == true) == true)
            throw new NotSupportedException("OpenCodeZen Decisions supports text input only.");

        var state = request.Input.Text is not null
            ? new DecisionInput(request.Input.Text)
            : new DecisionInput(JsonSerializer.SerializeToElement(new { messages = request.Input }, JsonOptions));
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
                _ => throw new ArgumentException("Unsupported OpenCodeZen decision question type.", nameof(request))
            };
        }

        var response = await DecisionRequestAsync(typedRequest, cancellationToken);
        var raw = response.ProviderMetadata![GetIdentifier()];
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var id = QuestionId(index);
            var question = request.Questions[index];
            var original = raw.GetProperty("answers").GetProperty(id);
            OpenAIDecisionAnswer answer = (question, response.Answers[id]) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) =>
                    new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) =>
                    new OpenAIDecisionChoiceAnswer
                    {
                        Choice = choice.Choices[ParseOptionIndex(selected.Choice)].Value,
                        Confidence = original.GetProperty("confidence").GetDouble(),
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
                        Confidence = original.GetProperty("confidence").GetDouble(),
                        Probabilities = score.Levels.Select((level, levelIndex) =>
                            new OpenAIDecisionScoreProbability
                            {
                                Label = level.Label,
                                Value = levelIndex / (score.Levels.Count - 1d),
                                Probability = scored.Probabilities![Index(levelIndex)]
                            }).ToArray()
                    },
                _ => throw new InvalidOperationException("Unexpected OpenCodeZen decision answer type.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }

        var inputTokens = response.Usage!.InputTokens!.Value;
        var outputTokens = response.Usage.OutputTokens!.Value;
        return new OpenAIDecisionResponse
        {
            Model = raw.GetProperty("model").GetString()!,
            Answers = answers,
            Usage = new()
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = checked(inputTokens + outputTokens)
            },
            AdditionalProperties = new()
            {
                ["providerMetadata"] = JsonSerializer.SerializeToElement(response.ProviderMetadata, JsonOptions),
                ["warnings"] = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        type = "other",
                        message = "OpenCodeZen does not report cached, cache-write, or reasoning tokens; OpenAI-compatible detail counters default to zero."
                    }
                }, JsonOptions)
            }
        };
    }

    private static JsonObject MapQuestions(Dictionary<string, DecisionQuestion> source)
    {
        var result = new JsonObject();
        foreach (var (id, question) in source)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null
                || question.Instructions.Value.ValueKind != JsonValueKind.String)
                throw new NotSupportedException("OpenCodeZen questions require IDs and text instructions.");

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = question.Instructions.Value.GetString()
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null }:
                    throw new NotSupportedException($"OpenCodeZen noul question '{id}' does not support criteria.");
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Count is < 2 or > MaxChoiceCount
                        || choice.Criteria.Any(item => string.IsNullOrWhiteSpace(item.Key)
                            || item.Value is not null && item.Value.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                        throw new ArgumentException($"OpenCodeZen choice question '{id}' requires 2–52 named options with text descriptions.", nameof(source));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, JsonOptions);
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Count is < 2 or > MaxScoreCount
                        || score.Criteria.Any(level => level is null || level.Value.ValueKind != JsonValueKind.String))
                        throw new ArgumentException($"OpenCodeZen score question '{id}' requires 2–10 text levels.", nameof(source));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, JsonOptions);
                    break;
                default:
                    throw new NotSupportedException($"OpenCodeZen does not support question type '{question.Type}'.");
            }
            result[id] = mapped;
        }
        return result;
    }

    private static DecisionResponse MapResponse(JsonElement body, DecisionRequest request, string model)
    {
        if (body.GetProperty("model").GetString() != model)
            throw new JsonException("Decision model mismatch.");
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
            if (answer.TryGetProperty("type", out var type) && type.GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var distribution = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!distribution.TryGetValue(selected, out var probability)
                        || probability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice must be a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    ReadProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(Index).ToArray();
                    var probabilities = ReadDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String))
                        throw new JsonException("Invalid score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1
                        || Math.Abs(value - expected) > 1e-6)
                        throw new JsonException("Invalid weighted score.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                default:
                    throw new JsonException("Unsupported question type.");
            }
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0)
            throw new JsonException("Invalid token usage.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SendDecisionAsync(
        JsonObject payload,
        Dictionary<string, string>? passthroughHeaders,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        foreach (var (name, value) in passthroughHeaders ?? [])
            request.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"OpenCodeZen decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            return (document.RootElement.Clone(), headers);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("OpenCodeZen returned an invalid decision response.", exception);
        }
    }

    private static string RemoveProviderPrefix(string model, string provider)
        => model.StartsWith(provider + "/", StringComparison.OrdinalIgnoreCase) ? model[(provider.Length + 1)..] : model;

    private static bool HasSdkImageFile(JsonElement state)
        => state.ValueKind == JsonValueKind.Array
            && state.EnumerateArray().Any(part =>
                part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var type) && type.GetString() == "file"
                && part.TryGetProperty("mediaType", out var mediaType)
                && mediaType.ValueKind == JsonValueKind.String
                && mediaType.GetString()!.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

    private static string QuestionId(int index) => "question-" + Index(index);
    private static string OptionId(int index) => "option-" + Index(index);
    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static int ParseOptionIndex(string option)
        => int.Parse(option["option-".Length..], CultureInfo.InvariantCulture);

    private static string DescribeLevel(string label, string? description)
        => string.IsNullOrEmpty(description) ? label : label + ": " + description;

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
