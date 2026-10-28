using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Upstage;

public partial class UpstageProvider
{
    private const string DecisionProvider = "upstage";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = AIHappey.Core.AI.DecisionRequestCompatibility.PrepareForProvider(request, GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var model = request.Model.StartsWith(DecisionProvider + "/", StringComparison.OrdinalIgnoreCase)
            ? request.Model[(DecisionProvider.Length + 1)..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (request.State is null)
            throw new ArgumentException("Upstage decision state is required.", nameof(request));
        if (request.Questions is null || request.Questions.Count == 0)
            throw new ArgumentException("Upstage requires at least one decision question.", nameof(request));
        if (request.ProviderOptions?.TryGetValue(DecisionProvider, out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new ArgumentException("Upstage Decisions supports no additional provider options.", nameof(request));

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Decision questions require a nonempty ID and instructions.", nameof(request));
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, DecisionJsonOptions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, DecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is not null && choice.Criteria.Count is >= 2 and <= 26
                    && choice.Criteria.Keys.All(key => !string.IsNullOrWhiteSpace(key)):
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is not null && score.Criteria.Count >= 2
                    && score.Criteria.All(level => level is not null):
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException($"Question '{id}' has unsupported criteria: choices require 2–26 named options and scores at least two nonnull levels.", nameof(request));
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, DecisionJsonOptions),
            ["questions"] = questions
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(DecisionProvider) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Upstage decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapDecisionResponse(body, request);
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
            throw new InvalidOperationException("Upstage returned an invalid decision response.", ex);
        }
    }

    private static DecisionResponse MapDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(body.GetProperty("model").GetString()))
            throw new JsonException("Missing decision model version.");
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
            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadDecisionProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadDecisionProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    var distribution = ReadDecisionDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (!distribution.TryGetValue(selected, out var probability) || probability + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Choice is not a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    ReadDecisionProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(DecisionIndex).ToArray();
                    var probabilities = ReadDecisionDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var level)
                            || !JsonElement.DeepEquals(level, score.Criteria[int.Parse(key, CultureInfo.InvariantCulture)]!.Value)))
                        throw new JsonException("Score legend must match the submitted rubric.");
                    var value = answer.GetProperty("score").GetDouble();
                    var expected = probabilities.Sum(p => int.Parse(p.Key, CultureInfo.InvariantCulture) * p.Value);
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6)
                        throw new JsonException("Invalid weighted score.");
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

    private static string DecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double ReadDecisionProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadDecisionDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadDecisionProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
