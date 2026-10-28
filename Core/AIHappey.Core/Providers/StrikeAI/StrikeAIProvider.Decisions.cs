using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StrikeAI;

public sealed partial class StrikeAIProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.StateFormat == "sdk" && request.State?.Value.ValueKind == JsonValueKind.Array
            && request.State.Value.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String && type.GetString() == "file"))
            throw new NotSupportedException("StrikeAI Decisions does not support SDK file inputs.");
        request = request.PrepareForProvider(GetIdentifier());
        var model = RemoveProviderPrefix(request.Model);
        if (request.Questions.Count > 32)
            throw new ArgumentException("StrikeAI accepts at most 32 questions per decision.", nameof(request));

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, DecisionJsonOptions),
            ["questions"] = MapQuestions(request.Questions)
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("StrikeAI provider options must be an object.", nameof(request));
            foreach (var option in options.EnumerateObject())
                if (option.Name != "return_raw_probabilities")
                    throw new NotSupportedException($"StrikeAI does not support provider option '{option.Name}'.");
                else if (option.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("StrikeAI return_raw_probabilities must be a boolean.", nameof(request));
            payload["options"] = JsonNode.Parse(options.GetRawText());
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"StrikeAI decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapResponse(body, request);
            var id = body.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(body.GetProperty("model").GetString()))
                throw new JsonException("StrikeAI decisions require a response ID and model.");
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                Id = id,
                ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Headers = headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("StrikeAI returned an invalid decision response.", exception);
        }
    }

    private static JsonObject MapQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (question.Instructions?.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(question.Instructions.Value.GetString()))
                throw new NotSupportedException($"StrikeAI question '{id}' requires text instructions.");
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = question.Instructions.Value.GetString()
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null }:
                    throw new NotSupportedException($"StrikeAI noul question '{id}' does not support criteria.");
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Count is < 2 or > 255 || choice.Criteria.Keys.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException($"StrikeAI choice question '{id}' requires 2-255 named options.", nameof(questions));
                    if (choice.Criteria.Values.Any(value => value is not null && value.Value.ValueKind != JsonValueKind.String))
                        throw new NotSupportedException($"StrikeAI choice question '{id}' supports only text or null descriptions.");
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Count is < 2 or > 10)
                        throw new ArgumentException($"StrikeAI score question '{id}' requires 2-10 ordered levels.", nameof(questions));
                    if (score.Criteria.Any(level => level is null || level.Value.ValueKind != JsonValueKind.String))
                        throw new NotSupportedException($"StrikeAI score question '{id}' requires text levels.");
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new NotSupportedException($"StrikeAI does not support question type '{question.Type}'.");
            }
            result[id] = mapped;
        }
        return result;
    }

    private static DecisionResponse MapResponse(JsonElement body, DecisionRequest request)
    {
        var native = body.GetProperty("answers");
        if (native.ValueKind != JsonValueKind.Object
            || native.EnumerateObject().Count() != request.Questions.Count
            || native.EnumerateObject().Select(answer => answer.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");
        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = native.GetProperty(id);
            if (answer.GetProperty("type").GetString() != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException("Answer type does not match question.");
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                {
                    ReadProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Unknown choice.");
                    var probabilities = ReadOptionalDistribution(answer, choice.Criteria.Keys);
                    if (probabilities is not null && probabilities[selected] + 1e-6 < probabilities.Values.Max())
                        throw new JsonException("Selected choice must have a maximal probability.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = probabilities };
                    break;
                }
                case DecisionScoreQuestion score:
                {
                    ReadProbability(answer.GetProperty("confidence"));
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1)
                        throw new JsonException("Score must be within the level range.");
                    var probabilities = ReadOptionalDistribution(answer, Enumerable.Range(0, score.Criteria.Count).Select(Index));
                    if (probabilities is not null
                        && Math.Abs(value - probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value)) > 1e-6)
                        throw new JsonException("Score must match the probability-weighted value.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                }
            }
        }
        var usage = body.GetProperty("usage");
        var input = usage.GetProperty("input_tokens").GetInt32();
        var output = usage.GetProperty("output_tokens").GetInt32();
        if (input < 0 || output < 0)
            throw new JsonException("Token usage cannot be negative.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private static Dictionary<string, double>? ReadOptionalDistribution(JsonElement answer, IEnumerable<string> keys)
    {
        if (!answer.TryGetProperty("probabilities", out var distribution) || distribution.ValueKind == JsonValueKind.Null)
            return null;
        if (distribution.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in distribution.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, ReadProbability(item.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized probability distribution.");
        return result;
    }

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);
}
