using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SurplusIntelligence;

public sealed partial class SurplusIntelligenceProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxDecisionBodyBytes = 10 * 1024 * 1024;

    private Task<OpenAIDecisionResponse> SendOpenAIDecisionAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelProviderDecisionExtensions.ValidateOpenAIDecisionRequest(request);
        ValidateDecisionLimits(request);
        request.Model = GetNativeModel(request.Model);
        ApplyAuthHeader();
        return _client.OpenAICompatibleDecisionRequestAsync(request, cancellationToken: cancellationToken);
    }

    private async Task<DecisionResponse> SendDecisionAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());

        if (!IsJevModel(request.Model))
        {
            var native = request.ToOpenAIDecisionRequest(GetIdentifier());
            ValidateDecisionLimits(native);
            native.Model = GetNativeModel(native.Model);
            ApplyAuthHeader();
            var rawInput = native.Input.Messages is not null ? request.State.Value : (JsonElement?)null;
            var result = await ModelProviderDecisionExtensions.SendDecisionRequestAsync(
                _client,
                native,
                "v1/decisions",
                rawInput,
                request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()),
                cancellationToken);
            return result.ToDecisionResponse(request, GetIdentifier());
        }

        var questions = MapJevQuestions(request.Questions);
        var state = JsonNode.Parse(request.State.Value.GetRawText())
            ?? throw new ArgumentException("Surplus Intelligence decision state cannot be null.", nameof(request));
        var payloadNode = new JsonObject
        {
            ["model"] = GetNativeModel(request.Model),
            ["state"] = state,
            ["questions"] = JsonSerializer.SerializeToNode(questions, DecisionJsonOptions)
        };
        AddJevProviderOptions(payloadNode, request.ProviderOptions);
        var payload = payloadNode.ToJsonString(DecisionJsonOptions);
        if (Encoding.UTF8.GetByteCount(payload) > MaxDecisionBodyBytes)
            throw new ArgumentException("Surplus Intelligence Decisions requests are limited to 10 MB.", nameof(request));

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(body)
                    ? $"Surplus Intelligence Decisions failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : $"Surplus Intelligence Decisions failed ({(int)response.StatusCode} {response.ReasonPhrase}): {body}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var answers = MapJevAnswers(root, request.Questions);
            var (inputTokens, outputTokens) = ReadJevUsage(root);
            return new DecisionResponse
            {
                Answers = answers,
                Usage = new DecisionUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
                Response = new()
                {
                    ModelId = ReadString(root, "model")?.ToModelId(GetIdentifier())
                        ?? throw new JsonException("Surplus Intelligence JEV response has no model."),
                    Timestamp = DateTimeOffset.UtcNow,
                    Headers = response.GetHeaders().ToDictionary(
                        static header => header.Key,
                        static header => (string?)header.Value,
                        StringComparer.OrdinalIgnoreCase),
                    Body = root.Clone()
                },
                Warnings = []
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Surplus Intelligence returned an invalid JEV Decisions response.", exception);
        }
    }

    private static Dictionary<string, object> MapJevQuestions(
        Dictionary<string, DecisionQuestion> questions)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (name, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(name) || question is null)
                throw new ArgumentException("Every JEV decision question requires a non-empty ID and a question.");
            if (name.Length > 128)
                throw new ArgumentException($"JEV question '{name}' exceeds the 128-character name limit.");

            var mapped = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = RenderDecisionInput(question.Instructions
                    ?? throw new ArgumentException($"JEV question '{name}' requires instructions."))
            };
            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    mapped["criteria"] = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["true"] = boolean.Criteria.True is null ? null : RenderDecisionInput(boolean.Criteria.True),
                        ["false"] = boolean.Criteria.False is null ? null : RenderDecisionInput(boolean.Criteria.False)
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    if (choice.Criteria.Count == 0 || choice.Criteria.Keys.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException($"JEV choice question '{name}' requires non-empty choices.");
                    if (choice.Criteria.Count > 64)
                        throw new ArgumentException($"JEV choice question '{name}' exceeds the 64-choice limit.");
                    mapped["criteria"] = choice.Criteria.ToDictionary(
                        static item => item.Key,
                        static item => item.Value is null ? null : RenderDecisionInput(item.Value),
                        StringComparer.Ordinal);
                    break;
                case DecisionScoreQuestion score:
                    if (score.Criteria.Count == 0)
                        throw new ArgumentException($"JEV score question '{name}' requires at least one level.");
                    if (score.Criteria.Count > 64)
                        throw new ArgumentException($"JEV score question '{name}' exceeds the 64-level limit.");
                    mapped["criteria"] = score.Criteria.Select(level => level is null
                        ? throw new ArgumentException($"JEV score question '{name}' cannot contain null levels.")
                        : RenderDecisionInput(level)).ToArray();
                    break;
                case DecisionBooleanQuestion:
                    break;
                default:
                    throw new NotSupportedException($"Surplus Intelligence Decisions does not support question type '{question.Type}'.");
            }
            result[name] = mapped;
        }

        if (result.Count is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(questions), "Surplus Intelligence accepts between 1 and 64 decision questions.");
        return result;
    }

    private static void AddJevProviderOptions(
        JsonObject body,
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("surplusintelligence", out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.surplusintelligence must be an object.", nameof(providerOptions));

        var supported = new HashSet<string>(
            ["provider", "providers_blocked", "provider_order", "si_route", "user"],
            StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
        {
            if (!supported.Contains(property.Name))
                throw new NotSupportedException($"Surplus Intelligence JEV Decisions does not support provider option '{property.Name}'.");
            if (property.Name == "user" && property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ArgumentException("Surplus Intelligence JEV user must be a string or null.", nameof(providerOptions));
            body[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
    }

    private static Dictionary<string, DecisionAnswer> MapJevAnswers(
        JsonElement body,
        Dictionary<string, DecisionQuestion> questions)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("answers", out var answerRoot)
            || answerRoot.ValueKind != JsonValueKind.Object
            || answerRoot.EnumerateObject().Count() != questions.Count)
            throw new JsonException("Surplus Intelligence JEV response must answer every question.");

        var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (name, question) in questions)
        {
            if (!answerRoot.TryGetProperty(name, out var answer) || answer.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Surplus Intelligence JEV response is missing answer '{name}'.");
            var answerType = ReadString(answer, "type");
            if (answerType == "refusal")
            {
                result[name] = new DecisionRefusalAnswer();
                continue;
            }

            switch (question, answerType)
            {
                case (DecisionBooleanQuestion, "noul"):
                    result[name] = new DecisionBooleanAnswer
                    {
                        Probability = ReadProbability(answer.GetProperty("noul"))
                    };
                    break;
                case (DecisionChoiceQuestion choice, "choice"):
                    var choiceProbabilities = ReadJevDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys);
                    var choiceValue = ReadString(answer, "choice")
                        ?? throw new JsonException($"Surplus Intelligence answer '{name}' has no selected choice.");
                    if (!choiceProbabilities.TryGetValue(choiceValue, out var selectedProbability)
                        || selectedProbability + 0.000001 < choiceProbabilities.Values.Max())
                        throw new JsonException($"Surplus Intelligence answer '{name}' selected a choice without maximal probability.");
                    result[name] = new DecisionChoiceAnswer
                    {
                        Choice = choiceValue,
                        Probabilities = choiceProbabilities
                    };
                    break;
                case (DecisionScoreQuestion score, "score"):
                    var expectedKeys = Enumerable.Range(0, score.Criteria.Count)
                        .Select(static index => index.ToString(CultureInfo.InvariantCulture)).ToArray();
                    var scoreProbabilities = ReadJevDistribution(answer.GetProperty("probabilities"), expectedKeys);
                    var expectedScore = scoreProbabilities.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (!answer.TryGetProperty("score", out var scoreValue)
                        || !scoreValue.TryGetDouble(out var reportedScore)
                        || !double.IsFinite(reportedScore)
                        || Math.Abs(expectedScore - reportedScore) > 0.001)
                        throw new JsonException($"Surplus Intelligence answer '{name}' has an invalid score.");
                    result[name] = new DecisionScoreAnswer
                    {
                        Score = reportedScore,
                        Probabilities = scoreProbabilities
                    };
                    break;
                default:
                    throw new JsonException($"Surplus Intelligence answer '{name}' does not match its question.");
            }
        }
        return result;
    }

    private static Dictionary<string, double> ReadJevDistribution(
        JsonElement value,
        IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Surplus Intelligence JEV probability map must be an object.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
        {
            if (!expected.Contains(item.Name) || !result.TryAdd(item.Name, ReadProbability(item.Value)))
                throw new JsonException("Surplus Intelligence JEV probabilities contain an unknown or duplicate choice.");
        }
        if (result.Count != expected.Count || Math.Abs(result.Values.Sum() - 1) > 0.000001)
            throw new JsonException("Surplus Intelligence JEV probabilities must cover all choices and sum to one.");
        return result;
    }

    private static double ReadProbability(JsonElement value)
    {
        if (!value.TryGetDouble(out var probability) || !double.IsFinite(probability) || probability is < 0 or > 1)
            throw new JsonException("Surplus Intelligence decision probabilities must be finite values in [0, 1].");
        return probability;
    }

    private static void ValidateDecisionLimits(OpenAIDecisionRequest request)
    {
        if (request.Questions.Count > 64)
            throw new ArgumentException("Surplus Intelligence accepts at most 64 decision questions.", nameof(request));

        foreach (var question in request.Questions)
        {
            if (question.Name?.Length > 128)
                throw new ArgumentException("Surplus Intelligence decision names are limited to 128 characters.", nameof(request));
            if (question is OpenAIDecisionChoiceQuestion { Choices.Count: > 64 })
                throw new ArgumentException("Surplus Intelligence choice questions are limited to 64 choices.", nameof(request));
            if (question is OpenAIDecisionScoreQuestion { Levels.Count: > 64 })
                throw new ArgumentException("Surplus Intelligence score questions are limited to 64 levels.", nameof(request));
        }

        if (JsonSerializer.SerializeToUtf8Bytes(request, DecisionJsonOptions).Length > MaxDecisionBodyBytes)
            throw new ArgumentException("Surplus Intelligence Decisions requests are limited to 10 MB.", nameof(request));
    }

    private static (int InputTokens, int OutputTokens) ReadJevUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("Surplus Intelligence JEV response requires usage.");
        var inputTokens = ReadUsageCount(usage, "input_tokens")
            ?? throw new JsonException("Surplus Intelligence JEV usage requires input_tokens.");
        var outputTokens = ReadUsageCount(usage, "output_tokens") ?? 0;
        return (inputTokens, outputTokens);
    }

    private static int? ReadUsageCount(JsonElement usage, string propertyName)
    {
        if (!usage.TryGetProperty(propertyName, out var value))
            return null;
        if (!value.TryGetInt32(out var count) || count < 0)
            throw new JsonException($"Surplus Intelligence JEV usage.{propertyName} must be a nonnegative integer.");
        return count;
    }

    private static string RenderDecisionInput(DecisionInput input)
        => input.Value.ValueKind == JsonValueKind.String
            ? input.Value.GetString()!
            : input.Value.GetRawText();

    private static string GetNativeModel(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        const string prefix = "surplusintelligence/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private static bool IsJevModel(string model)
    {
        var nativeModel = GetNativeModel(model);
        return nativeModel.StartsWith("jev-", StringComparison.OrdinalIgnoreCase)
            || nativeModel.StartsWith("typesafe/jev", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
