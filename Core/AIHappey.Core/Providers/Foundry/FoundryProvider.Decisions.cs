using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Foundry;

public partial class FoundryProvider
{
    private const string FoundryDecisionEndpoint = "providers/microsoft/v1/systemone";
    private static readonly JsonSerializerOptions FoundryDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = AIHappey.Core.AI.DecisionRequestCompatibility.PrepareForProvider(request, GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        var model = request.Model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase)
            ? request.Model[(GetIdentifier().Length + 1)..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("Foundry requires at least one decision question.", nameof(request));
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new ArgumentException("Foundry Decisions supports no additional provider options.", nameof(request));

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Decision questions require a nonempty ID and instructions.", nameof(request));

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, FoundryDecisionJsonOptions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, FoundryDecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: >= 2 }
                    && choice.Criteria.Keys.All(key => !string.IsNullOrWhiteSpace(key)):
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, FoundryDecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is { Count: >= 2 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, FoundryDecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException($"Question '{id}' has unsupported criteria: choices and scores require at least two options or levels.", nameof(request));
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, FoundryDecisionJsonOptions),
            ["questions"] = questions
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, FoundryDecisionEndpoint)
        {
            Content = JsonContent.Create(payload, options: FoundryDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapFoundryDecisionResponse(body, request);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value), Body = body.Clone()
            };
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Foundry returned an invalid decision response.", ex);
        }
    }

    private static DecisionResponse MapFoundryDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(body.GetProperty("model").GetString()))
            throw new JsonException("Missing decision model.");
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
            if (answer.TryGetProperty("type", out var type) && type.GetString() != expectedType)
                throw new JsonException("Answer type does not match question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadFoundryProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadFoundryProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var distribution = ReadFoundryDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!distribution.TryGetValue(selected, out var probability) || probability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Choice is not a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    ReadFoundryProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(FoundryDecisionIndex).ToArray();
                    var probabilities = ReadFoundryDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String))
                        throw new JsonException("Invalid score legend.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = probabilities.Sum(p => int.Parse(p.Key, CultureInfo.InvariantCulture) * p.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6)
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
            throw new JsonException("Invalid decision usage.");
        return new() { Answers = answers, Usage = new() { InputTokens = input, OutputTokens = output } };
    }

    private static string FoundryDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double ReadFoundryProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadFoundryDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadFoundryProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
