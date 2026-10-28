using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hanzo;

public partial class HanzoProvider
{
    private static readonly JsonSerializerOptions HanzoDecisionJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var prefix = GetIdentifier() + "/";
        var model = request.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? request.Model[prefix.Length..] : request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (request.State is null)
            throw new ArgumentException("Hanzo decision state is required.", nameof(request));
        if (request.State.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("Hanzo decision state must be a string, object, or array.", nameof(request));
        if (request.Questions is null || request.Questions.Count is < 1 or > 100)
            throw new ArgumentException("Hanzo supports 1 to 100 decision questions.", nameof(request));

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Decision questions require a nonempty ID and instructions.", nameof(request));

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonSerializer.SerializeToNode(question.Instructions, HanzoDecisionJsonOptions)
            };
            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, HanzoDecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion choice when choice.Criteria is { Count: >= 2 }:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, HanzoDecisionJsonOptions);
                    break;
                case DecisionScoreQuestion score when score.Criteria is { Count: >= 1 }:
                    if (score.Criteria.Any(level => level is null))
                        throw new ArgumentException($"Score question '{id}' cannot contain null levels.", nameof(request));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, HanzoDecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException($"Question '{id}' has unsupported criteria; choices require at least two labels and scores require at least one nonnull level.", nameof(request));
            }
            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonSerializer.SerializeToNode(request.State, HanzoDecisionJsonOptions),
            ["questions"] = questions
        };
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
            MergeHanzoDecisionOptions(payload, options);

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = JsonContent.Create(payload, options: HanzoDecisionJsonOptions)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Hanzo decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null, response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapHanzoDecisionResponse(body, request);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = body.GetProperty("model").GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = body.TryGetProperty("id", out var id) ? id.GetString() : headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Hanzo returned an invalid decision response.", exception);
        }
    }

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Hanzo Decisions requires the typed state/questions contract; its API refuses the OpenAI input/questions contract.");
    }

    private static void MergeHanzoDecisionOptions(JsonObject payload, JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.hanzo must be an object.", nameof(options));

        foreach (var property in options.EnumerateObject())
        {
            if (new[] { "model", "state", "questions" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Hanzo decision options cannot override '{property.Name}'.", nameof(options));

            switch (property.Name)
            {
                case "provider":
                case "trace":
                    break;
                case "session_id":
                case "user":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 256)
                        throw new ArgumentException($"Hanzo '{property.Name}' must be a string of at most 256 characters.", nameof(options));
                    break;
                default:
                    throw new ArgumentException($"Hanzo Decisions does not support provider option '{property.Name}'.", nameof(options));
            }

            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static DecisionResponse MapHanzoDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString()))
            throw new JsonException("A Hanzo decision response requires a model.");

        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Answers must cover every question exactly once.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = nativeAnswers.GetProperty(id);
            if (answer.ValueKind != JsonValueKind.Object
                || !answer.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != (question is DecisionBooleanQuestion ? "noul" : question.Type))
                throw new JsonException("Answer type does not match question.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadHanzoDecisionProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadHanzoDecisionProbability(answer.GetProperty("confidence"));
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException("Unknown selected choice.");
                    var distribution = ReadHanzoDecisionDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    if (distribution[selected] + 1e-6 < distribution.Values.Max())
                        throw new JsonException("Selected choice is not a maximal option.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = distribution };
                    break;
                case DecisionScoreQuestion score:
                    ReadHanzoDecisionProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count).Select(HanzoDecisionIndex).ToArray();
                    var probabilities = ReadHanzoDecisionDistribution(answer.GetProperty("probabilities"), keys);
                    var legend = answer.GetProperty("legend");
                    if (legend.ValueKind != JsonValueKind.Object || legend.EnumerateObject().Count() != keys.Length
                        || keys.Any(key => !legend.TryGetProperty(key, out var label) || label.ValueKind != JsonValueKind.String))
                        throw new JsonException("Invalid score legend.");
                    var value = ReadHanzoDecisionNumber(answer.GetProperty("score"));
                    var expected = probabilities.Sum(item => int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (value < 0 || value > keys.Length - 1 || Math.Abs(value - expected) > 1e-6)
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

    private static string HanzoDecisionIndex(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static double ReadHanzoDecisionNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new JsonException("Expected a finite decision number.");
        return number;
    }

    private static double ReadHanzoDecisionProbability(JsonElement value)
    {
        var number = ReadHanzoDecisionNumber(value);
        if (number is < 0 or > 1)
            throw new JsonException("Probability must be in [0,1].");
        return number;
    }

    private static Dictionary<string, double> ReadHanzoDecisionDistribution(
        JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadHanzoDecisionProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Incomplete or unnormalized distribution.");
        return result;
    }
}
