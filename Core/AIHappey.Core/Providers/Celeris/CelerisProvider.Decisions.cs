using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Celeris;

public sealed partial class CelerisProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var pathModel = ModelSegment(request.Model);
        var upstreamRequest = new OpenAIDecisionRequest
        {
            Input = request.Input,
            Model = pathModel,
            Questions = request.Questions,
            SafetyIdentifier = request.SafetyIdentifier,
            AdditionalProperties = request.AdditionalProperties
        };

        ApplyAuthHeader();
        var response = await _client.OpenAICompatibleDecisionRequestAsync(
            upstreamRequest,
            endpoint: $"{pathModel}/v1/decisions",
            cancellationToken: cancellationToken);
        response.Model = request.Model;
        return response;
    }

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentNullException.ThrowIfNull(request.State);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && (providerOptions.ValueKind != JsonValueKind.Object || providerOptions.EnumerateObject().Any()))
            throw new NotSupportedException("Celeris Decisions does not support provider options.");

        var nativeModel = RemoveProviderPrefix(request.Model);
        var model = ModelSegment(nativeModel);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = MapQuestions(request.Questions)
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{model}/v1/systemone")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            httpRequest.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Celeris System One request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement.Clone();
            var result = MapSystemOneResponse(body, request, model);
            headers.TryGetValue("x-request-id", out var requestId);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body);
            result.Response = new()
            {
                ModelId = nativeModel.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = requestId,
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value),
                Body = body
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Celeris returned an invalid System One response.", exception);
        }
    }

    private static JsonObject MapQuestions(Dictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (id, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null
                || question.Instructions.Value.ValueKind != JsonValueKind.String)
                throw new NotSupportedException("Celeris System One questions require IDs and text instructions.");

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = question.Instructions.Value.GetString()
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null }:
                    throw new NotSupportedException($"Celeris boolean question '{id}' does not support criteria.");
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Count < 2)
                        throw new ArgumentException($"Celeris choice question '{id}' requires at least two options.", nameof(questions));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Count < 2)
                        throw new ArgumentException($"Celeris score question '{id}' requires at least two levels.", nameof(questions));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new NotSupportedException($"Celeris does not support question type '{question.Type}'.");
            }
            result[id] = mapped;
        }
        return result;
    }

    private static DecisionResponse MapSystemOneResponse(
        JsonElement body,
        DecisionRequest request,
        string model)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("model", out var returnedModel)
            || returnedModel.ValueKind != JsonValueKind.String
            || returnedModel.GetString() != model)
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
            if (!answer.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException("Answer type does not match question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                {
                    var selected = answer.GetProperty("choice").GetString()
                        ?? throw new JsonException("Missing choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Unknown choice.");
                    var probabilities = answer.TryGetProperty("probabilities", out var distribution)
                        ? ReadDistribution(distribution, choice.Criteria.Keys)
                        : null;
                    if (probabilities is not null
                        && probabilities[selected] + 1e-6 < probabilities.Values.Max())
                        throw new JsonException("Selected choice must have a maximal probability.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = probabilities };
                    break;
                }
                case DecisionScoreQuestion score:
                {
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1)
                        throw new JsonException("Invalid score.");
                    var probabilities = answer.TryGetProperty("probabilities", out var distribution)
                        ? ReadDistribution(distribution, Enumerable.Range(0, score.Criteria.Count).Select(Index))
                        : null;
                    if (probabilities is not null)
                    {
                        var weightedScore = probabilities.Sum(item =>
                            int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                        if (Math.Abs(value - weightedScore) > 1e-6)
                            throw new JsonException("Score must match the probability-weighted value.");
                    }
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = probabilities };
                    break;
                }
                default:
                    throw new JsonException("Unsupported question type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        if (inputTokens < 0 || outputTokens < 0)
            throw new JsonException("Invalid token usage.");
        return new()
        {
            Answers = answers,
            Usage = new() { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }

    private static Dictionary<string, double> ReadDistribution(
        JsonElement value,
        IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, ReadProbability(item.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
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

    private string RemoveProviderPrefix(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }
}
