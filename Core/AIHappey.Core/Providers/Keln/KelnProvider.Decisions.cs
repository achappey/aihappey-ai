using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Keln;

public sealed partial class KelnProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        var model = NativeModel(request.Model);
        if (request.Questions.Count > 400)
            throw new ArgumentOutOfRangeException(nameof(request), "Keln accepts at most 400 decision questions.");
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true
            && (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any()))
            throw new NotSupportedException("Keln Decisions does not document provider options.");

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (question.Instructions?.Value.ValueKind != JsonValueKind.String)
                throw new NotSupportedException("Keln decision instructions must be text.");
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = question.Instructions.Value.GetString()
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, DecisionJsonOptions);
                    break;
                case DecisionBooleanQuestion:
                    break;
                case DecisionChoiceQuestion choice:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new NotSupportedException("Keln supports only noul, choice and score questions.");
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = questions
        };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = JsonContent.Create(payload, options: DecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            httpRequest.Headers.TryAddWithoutValidation(name, value);
        ApplyAuthHeader();
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Keln Decisions request failed ({(int)response.StatusCode}): {raw}", null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var responseModel = body.GetProperty("model").GetString();
            if (string.IsNullOrWhiteSpace(responseModel))
                throw new JsonException("Decision response omitted its model.");
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
                    throw new JsonException("Decision answer type mismatch.");
                switch (question)
                {
                    case DecisionBooleanQuestion:
                        answers[id] = new DecisionBooleanAnswer { Probability = Probability(answer.GetProperty("noul")) };
                        break;
                    case DecisionChoiceQuestion choice:
                        var selected = answer.GetProperty("choice").GetString()!;
                        if (!choice.Criteria.ContainsKey(selected))
                            throw new JsonException("Unknown choice.");
                        var probabilities = answer.TryGetProperty("probabilities", out var choiceDistribution)
                            ? Distribution(choiceDistribution, choice.Criteria.Keys) : null;
                        if (probabilities is not null && probabilities[selected] + 1e-6 < probabilities.Values.Max())
                            throw new JsonException("Choice must have maximal probability.");
                        answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = probabilities };
                        break;
                    case DecisionScoreQuestion score:
                        var value = answer.GetProperty("score").GetDouble();
                        var distribution = answer.TryGetProperty("probabilities", out var scoreDistribution)
                            ? Distribution(scoreDistribution, Enumerable.Range(0, score.Criteria.Count).Select(Index)) : null;
                        var weighted = distribution?.Sum(p => int.Parse(p.Key, CultureInfo.InvariantCulture) * p.Value);
                        if (!double.IsFinite(value) || value < 0 || value > score.Criteria.Count - 1
                            || (weighted.HasValue && Math.Abs(value - weighted.Value) > 1e-6))
                            throw new JsonException("Invalid probability-weighted score.");
                        answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = distribution };
                        break;
                }
            }
            var usage = body.GetProperty("usage");
            var input = usage.GetProperty("input_tokens").GetInt32();
            var output = usage.GetProperty("output_tokens").GetInt32();
            if (input < 0 || output < 0)
                throw new JsonException("Negative token usage.");
            var headers = response.GetHeaders();
            headers.TryGetValue("x-request-id", out var requestId);
            return new DecisionResponse
            {
                Answers = answers,
                Usage = new() { InputTokens = input, OutputTokens = output },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone()),
                Response = new()
                {
                    ModelId = responseModel.ToModelId(GetIdentifier()), Id = requestId, Timestamp = DateTimeOffset.UtcNow,
                    Headers = headers.ToDictionary(p => p.Key, p => (string?)p.Value), Body = body.Clone()
                }
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Keln returned an invalid System One decision response.", exception);
        }
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        if (request.SafetyIdentifier is not null || request.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("Keln Decisions does not support safety_identifier or additional OpenAI decision options.");
        var questions = new Dictionary<string, DecisionQuestion>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            DecisionQuestion mapped = question switch
            {
                OpenAIDecisionPredicateQuestion => new DecisionBooleanQuestion(),
                OpenAIDecisionChoiceQuestion choice => new DecisionChoiceQuestion
                {
                    // Internal IDs preserve both boolean and string choice values without collisions.
                    Criteria = choice.Choices.Select((v, i) => (v, i)).ToDictionary(p => Index(p.i),
                        p => (DecisionInput?)new DecisionInput(p.v.Description is null ? p.v.Value.Value.ToString()
                            : $"{p.v.Value.Value}: {p.v.Description}"))
                },
                OpenAIDecisionScoreQuestion score => new DecisionScoreQuestion
                {
                    Criteria = score.Levels.Select(v => (DecisionInput?)new DecisionInput(v.Description is null
                        ? v.Label : $"{v.Label}: {v.Description}")).ToArray()
                },
                _ => throw new NotSupportedException("Unsupported OpenAI decision question.")
            };
            mapped.Instructions = question.Instructions;
            questions[Index(index)] = mapped;
        }
        var state = request.Input.Text is not null ? new DecisionInput(request.Input.Text)
            : new DecisionInput(JsonSerializer.SerializeToElement(request.Input, DecisionJsonOptions));
        var result = await DecisionRequestAsync(new() { Model = request.Model, State = state, Questions = questions }, cancellationToken);
        var answers = new List<OpenAIDecisionAnswer>();
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            var native = result.Answers[Index(index)];
            if (native is DecisionChoiceAnswer { Probabilities: null } or DecisionScoreAnswer { Probabilities: null })
                throw new NotSupportedException("The OpenAI Decisions contract requires distributions, but Keln omitted probabilities.");
            OpenAIDecisionAnswer answer = (question, native) switch
            {
                (OpenAIDecisionPredicateQuestion, DecisionBooleanAnswer boolean) => new OpenAIDecisionPredicateAnswer { Probability = boolean.Probability },
                (OpenAIDecisionChoiceQuestion choice, DecisionChoiceAnswer selected) => new OpenAIDecisionChoiceAnswer
                {
                    Choice = choice.Choices[int.Parse(selected.Choice, CultureInfo.InvariantCulture)].Value,
                    Confidence = selected.Probabilities![selected.Choice],
                    Probabilities = choice.Choices.Select((v, i) => new OpenAIDecisionChoiceProbability
                    {
                        Value = v.Value, Probability = selected.Probabilities[Index(i)]
                    }).ToArray()
                },
                (OpenAIDecisionScoreQuestion score, DecisionScoreAnswer selected) => new OpenAIDecisionScoreAnswer
                {
                    Score = selected.Score, Confidence = selected.Probabilities!.Values.Max(),
                    Probabilities = score.Levels.Select((v, i) => new OpenAIDecisionScoreProbability
                    {
                        Label = v.Label, Value = i, Probability = selected.Probabilities[Index(i)]
                    }).ToArray()
                },
                _ => throw new InvalidOperationException("Unexpected decision answer.")
            };
            answer.Name = question.Name;
            answers.Add(answer);
        }
        var usage = result.Response?.Body is JsonElement raw
            ? raw.GetProperty("usage").Deserialize<OpenAIDecisionUsage>(DecisionJsonOptions)! : new OpenAIDecisionUsage();
        usage.InputTokens = result.Usage!.InputTokens!.Value;
        usage.OutputTokens = result.Usage.OutputTokens!.Value;
        usage.TotalTokens = checked(usage.InputTokens + usage.OutputTokens);
        return new OpenAIDecisionResponse
        {
            Model = result.Response?.Body is JsonElement responseBody
                ? responseBody.GetProperty("model").GetString()!
                : NativeModel(request.Model),
            Answers = answers,
            Usage = usage
        };
    }

    private static Dictionary<string, double> Distribution(JsonElement value, IEnumerable<string> keys)
    {
        var expected = keys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>();
        foreach (var item in value.EnumerateObject())
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, Probability(item.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }

    private static double Probability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);
}
