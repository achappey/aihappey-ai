using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ValidateNonEmptyString(request.State, "state", nameof(request));

        var model = NormalizeDecisionModel(request.Model);
        if (model is null)
            throw new ArgumentException("RouterPlus Decisions supports routerplus/decider-2b and routerplus/kev-4b.", nameof(request));
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new ArgumentException("RouterPlus Decisions does not document provider-specific options.", nameof(request));

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("RouterPlus decision questions require a nonempty ID and instructions.", nameof(request));
            ValidateNonEmptyString(question.Instructions, $"instructions for '{id}'", nameof(request));

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
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: >= 1 and <= 255 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is { Count: >= 2 and <= 10 }
                    && score.Criteria.All(level => level is not null):
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException(
                        $"Question '{id}' is invalid: choice questions require 1 to 255 options and score questions require 2 to 10 nonnull levels.",
                        nameof(request));
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, DecisionJsonOptions),
            ["questions"] = questions
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"RouterPlus decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var responseModel = body.GetProperty("model").GetString();
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new JsonException("RouterPlus returned a different decision model.");

            var answersElement = body.GetProperty("answers");
            if (answersElement.ValueKind != JsonValueKind.Object
                || answersElement.EnumerateObject().Count() != request.Questions.Count
                || answersElement.EnumerateObject().Select(item => item.Name)
                    .Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
                throw new JsonException("RouterPlus answers must cover every question exactly once.");

            var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
            foreach (var (id, question) in request.Questions)
            {
                var answer = answersElement.GetProperty(id);
                var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
                if (answer.TryGetProperty("type", out var type)
                    && !string.Equals(type.GetString(), expectedType, StringComparison.Ordinal))
                    throw new JsonException($"RouterPlus returned an unexpected answer type for '{id}'.");

                switch (question)
                {
                    case DecisionBooleanQuestion:
                        answers[id] = new DecisionBooleanAnswer
                        {
                            Probability = ReadProbability(answer.GetProperty("noul"))
                        };
                        break;
                    case DecisionChoiceQuestion choice:
                        ReadProbability(answer.GetProperty("confidence"));
                        var selected = answer.GetProperty("choice").GetString()
                            ?? throw new JsonException("RouterPlus returned no selected choice.");
                        var choiceProbabilities = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                        if (!choiceProbabilities.TryGetValue(selected, out var selectedProbability)
                            || selectedProbability + 1e-6 < choiceProbabilities.Values.Max())
                            throw new JsonException("RouterPlus selected a choice that is not maximal.");
                        answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = choiceProbabilities };
                        break;
                    case DecisionScoreQuestion score:
                        ReadProbability(answer.GetProperty("confidence"));
                        var keys = Enumerable.Range(0, score.Criteria.Count).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();
                        var scoreProbabilities = ReadDistribution(answer.GetProperty("probabilities"), keys);
                        var value = answer.GetProperty("score").GetDouble();
                        var expectedScore = scoreProbabilities.Sum(item =>
                            int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                        if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1
                            || Math.Abs(value - expectedScore) > 1e-6)
                            throw new JsonException("RouterPlus returned an invalid weighted score.");
                        var legend = answer.GetProperty("legend");
                        if (legend.ValueKind != JsonValueKind.Object
                            || legend.EnumerateObject().Count() != keys.Length
                            || keys.Any(key => !legend.TryGetProperty(key, out var label)
                                || label.ValueKind != JsonValueKind.String))
                            throw new JsonException("RouterPlus returned an invalid score legend.");
                        answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = scoreProbabilities };
                        break;
                    default:
                        throw new JsonException("RouterPlus returned an unsupported question type.");
                }
            }

            var usage = body.GetProperty("usage");
            var inputTokens = usage.GetProperty("input_tokens").GetInt32();
            var outputTokens = usage.GetProperty("output_tokens").GetInt32();
            if (inputTokens < 0 || outputTokens < 0)
                throw new JsonException("RouterPlus returned invalid decision usage.");

            var headers = response.GetHeaders();
            return new DecisionResponse
            {
                Answers = answers,
                Usage = new DecisionUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new DecisionResponseMetadata
                {
                    Id = headers.GetValueOrDefault("x-request-id"),
                    Timestamp = DateTimeOffset.UtcNow,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Headers = headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
                    Body = body.Clone()
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("RouterPlus returned an invalid decision response.", exception);
        }
    }

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("RouterPlus Decisions accepts native System One questions, not OpenAI Decisions requests.");

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("RouterPlus probabilities must be finite values in [0, 1].");
        return number;
    }

    private static void ValidateNonEmptyString(DecisionInput input, string name, string parameterName)
    {
        if (input.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(input.Value.GetString()))
            throw new ArgumentException($"RouterPlus decision {name} must not be empty.", parameterName);
    }

    private static string? NormalizeDecisionModel(string model)
    {
        var normalized = NormalizeModelId(model);
        foreach (var supported in new[] { Decider2BModel, Kev4BModel })
        {
            if (string.Equals(normalized, supported, StringComparison.Ordinal))
                return supported;

            var prefix = supported + "-";
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)
                && normalized.Length == prefix.Length + 8
                && DateOnly.TryParseExact(
                    normalized[prefix.Length..],
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
                return supported;
        }

        return null;
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("RouterPlus probability distributions must be objects.");

        var distribution = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!expected.Contains(item.Name) || !distribution.TryAdd(item.Name, ReadProbability(item.Value)))
                throw new JsonException("RouterPlus returned an unknown or duplicate probability key.");

        if (distribution.Count != expected.Count || Math.Abs(distribution.Values.Sum() - 1) > 1e-6)
            throw new JsonException("RouterPlus returned an incomplete or unnormalized probability distribution.");
        return distribution;
    }
}
