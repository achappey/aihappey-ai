using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
        => ExecuteOpenRouterDecisionAsync(request, false, cancellationToken);

    // A single ordered level is valid in the OpenAI contract and OpenRouter schema,
    // but not in the SDK contract. Only the OpenAI adapter enables it.
    private async Task<DecisionResponse> ExecuteOpenRouterDecisionAsync(
        DecisionRequest request, bool allowSingleScoreLevel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var prefix = GetIdentifier() + "/";
        var model = request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? request.Model[prefix.Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("At least one decision question is required.", nameof(request));

        List<object> warnings = [];
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Decision questions require a nonempty ID and instructions.", nameof(request));
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, OpenRouterDecisionJsonOptions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                    {
                        mapped["criteria"] = new JsonObject
                        {
                            ["true"] = OpenRouterDecisionGuidance(boolean.Criteria.True),
                            ["false"] = OpenRouterDecisionGuidance(boolean.Criteria.False)
                        };
                        if (boolean.Criteria.True is null || boolean.Criteria.False is null)
                            warnings.Add(OpenRouterDecisionWarning("Null or omitted boolean guidance is forwarded as an empty string.", id));
                    }
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: > 0 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, OpenRouterDecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is not null
                    && score.Criteria.Count >= (allowSingleScoreLevel ? 1 : 2):
                    mapped["criteria"] = new JsonArray(score.Criteria.Select(OpenRouterDecisionGuidance).ToArray());
                    if (score.Criteria.Any(level => level is null))
                        warnings.Add(OpenRouterDecisionWarning("Null score guidance is forwarded as an empty string, preserving every level.", id));
                    break;
                default:
                    throw new ArgumentException($"Question '{id}' has an unsupported type or invalid criteria.", nameof(request));
            }
            questions.Add(id, mapped);
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, OpenRouterDecisionJsonOptions),
            ["questions"] = questions
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
            MergeOpenRouterDecisionOptions(payload, options, warnings);

        // BaseAddress already contains /api/. Decisions is not a /v1 endpoint.
        using var message = new HttpRequestMessage(HttpMethod.Post, "alpha/decisions")
        {
            Content = JsonContent.Create(payload, options: OpenRouterDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapOpenRouterDecisionResponse(body, request);
            var headers = response.GetHeaders();
            result.Warnings = warnings;
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            if (TryGetUsageCost(body.GetProperty("usage")) is { } cost)
                result.ProviderMetadata["gateway"] = JsonSerializer.SerializeToElement(new { cost }, OpenRouterDecisionJsonOptions);
            result.Response = new()
            {
                ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = body.TryGetProperty("id", out var id) ? id.GetString() : headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("OpenRouter returned an invalid decision response.", exception);
        }
    }

    private static JsonNode OpenRouterDecisionGuidance(DecisionInput? input)
        => input is null ? JsonValue.Create("")! : JsonSerializer.SerializeToNode(input, OpenRouterDecisionJsonOptions)!;

    private static object OpenRouterDecisionWarning(string message, string? feature = null)
        => new { type = "other", message, feature };

    private static void MergeOpenRouterDecisionOptions(JsonObject payload, JsonElement options, List<object> warnings)
    {
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.openrouter must be an object.");
        foreach (var property in options.EnumerateObject())
        {
            if (new[] { "model", "state", "questions", "input" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"OpenRouter decision options cannot override '{property.Name}'.");
            switch (property.Name)
            {
                case "provider":
                    if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                        throw new ArgumentException("OpenRouter provider preferences must be an object or null.");
                    break;
                case "trace":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("OpenRouter trace must be an object.");
                    break;
                case "session_id":
                case "user":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 256)
                        throw new ArgumentException($"OpenRouter '{property.Name}' must be a string of at most 256 characters.");
                    break;
                default:
                    warnings.Add(OpenRouterDecisionWarning($"OpenRouter Decisions does not support '{property.Name}'; it was not forwarded.", property.Name));
                    continue;
            }
            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static DecisionResponse MapOpenRouterDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(body.GetProperty("model").GetString()))
            throw new JsonException("A decision response requires a model.");
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = nativeAnswers.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (answer.GetProperty("type").GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");
            if (answer.TryGetProperty("confidence", out var confidence))
                ReadOpenRouterDecisionProbability(confidence);
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadOpenRouterDecisionProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Unknown selected choice.");
                    var distribution = answer.TryGetProperty("probabilities", out var choiceProbabilities)
                        ? ReadOpenRouterDecisionDistribution(choiceProbabilities, choice.Criteria.Keys) : null;
                    if (distribution is not null && distribution[selected] + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice is not a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(OpenRouterDecisionIndex).ToArray();
                    var probabilities = answer.TryGetProperty("probabilities", out var scoreProbabilities)
                        ? ReadOpenRouterDecisionDistribution(scoreProbabilities, keys) : null;
                    var value = ReadOpenRouterDecisionNumber(answer.GetProperty("score"));
                    if (value < 0 || value > keys.Length - 1
                        || (probabilities is not null && Math.Abs(value - probabilities.Sum(p => int.Parse(p.Key, CultureInfo.InvariantCulture) * p.Value)) > 1e-6))
                        throw new JsonException("Invalid weighted score.");
                    if (answer.TryGetProperty("legend", out var legend))
                    {
                        if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                            || keys.Any(key => !legend.TryGetProperty(key, out var label)
                                || label.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)))
                            throw new JsonException("Invalid score legend.");
                    }
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                default: throw new JsonException("Unsupported question type.");
            }
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0)
            throw new JsonException("Invalid decision usage.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private static string OpenRouterDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double ReadOpenRouterDecisionNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Expected a finite decision number.");
        return number;
    }

    private static double ReadOpenRouterDecisionProbability(JsonElement value)
    {
        var number = ReadOpenRouterDecisionNumber(value);
        if (number is < 0 or > 1)
            throw new JsonException("Probability must be in [0,1].");
        return number;
    }

    private static Dictionary<string, double> ReadOpenRouterDecisionDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadOpenRouterDecisionProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
