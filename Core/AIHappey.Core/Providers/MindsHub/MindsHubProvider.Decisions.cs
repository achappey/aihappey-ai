using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MindsHub;

public sealed partial class MindsHubProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<DecisionResponse> ExecuteDecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.State);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProviderOptions is { Count: > 0 })
            throw new NotSupportedException("MindsHub Decisions does not document provider options.");
        if (request.StateFormat == "sdk" && ContainsSdkImage(request.State))
            throw new NotSupportedException("MindsHub Decisions does not support image input.");

        request = DecisionRequestCompatibility.PrepareForProvider(
            request,
            GetIdentifier(),
            requiresInstructions: false);

        var body = new JsonObject
        {
            ["model"] = RemoveProviderPrefix(request.Model),
            ["state"] = JsonNode.Parse(request.State.Value.GetRawText()),
            ["questions"] = MapQuestions(request.Questions)
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/decisions")
        {
            Content = new StringContent(body.ToJsonString(DecisionJsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new("Bearer", ResolveKey());
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        using var response = await _client.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                FormatDecisionError(response, raw, headers),
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            return MapDecisionResponse(document.RootElement, request, headers);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("MindsHub returned an invalid Decisions response.", exception);
        }
    }

    private string ResolveKey()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No MindsHub API key.");
        return key;
    }

    private static string FormatDecisionError(
        HttpResponseMessage response,
        string body,
        IDictionary<string, string> headers)
    {
        var details = new List<string>();
        if (headers.TryGetValue("x-request-id", out var requestId))
            details.Add($"request {requestId}");
        if (headers.TryGetValue("retry-after", out var retryAfter))
            details.Add($"Retry-After: {retryAfter}");
        if (headers.TryGetValue("x-mindshub-recovery-url", out var recoveryUrl))
            details.Add($"recovery: {recoveryUrl}");

        var suffix = details.Count == 0 ? "" : $" [{string.Join("; ", details)}]";
        return $"MindsHub decision request failed ({(int)response.StatusCode} {response.ReasonPhrase}){suffix}: {body}";
    }

    private static JsonObject MapQuestions(IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        var result = new JsonObject();
        foreach (var (name, question) in questions)
        {
            var mapped = JsonNode.Parse(JsonSerializer.Serialize(question, DecisionJsonOptions)) as JsonObject
                ?? throw new JsonException($"MindsHub decision question '{name}' is invalid.");
            mapped["type"] = question switch
            {
                DecisionBooleanQuestion => "noul",
                DecisionChoiceQuestion => "choice",
                DecisionScoreQuestion => "score",
                _ => throw new NotSupportedException($"MindsHub does not support decision question type '{question.Type}'.")
            };

            if (question is DecisionScoreQuestion score
                && score.Criteria.Any(level => level is null))
                throw new ArgumentException($"MindsHub score question '{name}' cannot contain null levels.");
            if (question.Instructions is null && (question switch
                {
                    DecisionBooleanQuestion boolean => boolean.Criteria?.True is null && boolean.Criteria?.False is null,
                    DecisionChoiceQuestion choice => choice.Criteria.Values.All(description => description is null),
                    DecisionScoreQuestion score => score.Criteria.All(level => level is null),
                    _ => true
                }))
                throw new ArgumentException($"MindsHub decision question '{name}' requires instructions or meaningful criteria.");

            result[name] = mapped;
        }
        return result;
    }

    private static bool ContainsSdkImage(DecisionInput state)
    {
        if (state.Value.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var part in state.Value.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "file"
                || !part.TryGetProperty("mediaType", out var mediaType)
                || mediaType.ValueKind != JsonValueKind.String)
                continue;

            if (mediaType.GetString()!.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private DecisionResponse MapDecisionResponse(
        JsonElement root,
        DecisionRequest request,
        IDictionary<string, string> headers)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(model.GetString())
            || !root.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Object)
            throw new JsonException("MindsHub decision response must include model and answers.");

        var mappedAnswers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (name, question) in request.Questions)
        {
            if (!answers.TryGetProperty(name, out var answer))
                throw new JsonException($"MindsHub decision response is missing answer '{name}'.");

            mappedAnswers[name] = MapAnswer(name, question, answer);
        }
        if (answers.EnumerateObject().Count() != mappedAnswers.Count)
            throw new JsonException("MindsHub returned an unexpected decision answer.");

        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new JsonException("MindsHub decision response must include usage.");
        var inputTokens = ReadUsage(usage, "input_tokens")
            ?? throw new JsonException("MindsHub decision response must include usage.input_tokens.");
        var outputTokens = ReadUsage(usage, "output_tokens")
            ?? throw new JsonException("MindsHub decision response must include usage.output_tokens.");

        var responseBody = root.Clone();
        return new DecisionResponse
        {
            Answers = mappedAnswers,
            Usage = new DecisionUsage { InputTokens = inputTokens, OutputTokens = outputTokens },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(responseBody),
            Response = new DecisionResponseMetadata
            {
                ModelId = model.GetString()!.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = responseBody
            },
            Warnings = []
        };
    }

    private static DecisionAnswer MapAnswer(string name, DecisionQuestion question, JsonElement answer)
    {
        if (answer.ValueKind != JsonValueKind.Object
            || !answer.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
            throw new JsonException($"MindsHub answer '{name}' has no type.");

        return (question, type.GetString()) switch
        {
            (DecisionBooleanQuestion, "noul") => MapBooleanAnswer(answer),
            (DecisionChoiceQuestion choice, "choice") => MapChoiceAnswer(answer, choice),
            (DecisionScoreQuestion score, "score") => MapScoreAnswer(answer, score),
            _ => throw new JsonException($"MindsHub answer '{name}' type does not match its question.")
        };
    }

    private static DecisionBooleanAnswer MapBooleanAnswer(JsonElement answer)
    {
        var probability = ReadDouble(answer, "noul");
        if (probability is < 0 or > 1)
            throw new JsonException("MindsHub noul answers must be in [0, 1].");
        return new DecisionBooleanAnswer { Probability = probability };
    }

    private static DecisionChoiceAnswer MapChoiceAnswer(
        JsonElement answer,
        DecisionChoiceQuestion question)
    {
        var choice = ReadString(answer, "choice");
        var expected = question.Criteria.Keys.ToHashSet(StringComparer.Ordinal);
        if (!expected.Contains(choice))
            throw new JsonException("MindsHub selected a choice absent from the submitted criteria.");

        var probabilities = ReadProbabilities(answer)
            ?? throw new JsonException("MindsHub choice answers must include probabilities.");
        ValidateDistribution(probabilities, expected);
        return new DecisionChoiceAnswer { Choice = choice, Probabilities = probabilities };
    }

    private static DecisionScoreAnswer MapScoreAnswer(
        JsonElement answer,
        DecisionScoreQuestion question)
    {
        var score = ReadDouble(answer, "score");
        if (score < 0 || score > question.Criteria.Count - 1)
            throw new JsonException("MindsHub score answer is outside the submitted scale.");

        var expected = Enumerable.Range(0, question.Criteria.Count)
            .Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet(StringComparer.Ordinal);
        var probabilities = ReadProbabilities(answer)
            ?? throw new JsonException("MindsHub score answers must include probabilities.");
        ValidateDistribution(probabilities, expected);
        return new DecisionScoreAnswer { Score = score, Probabilities = probabilities };
    }

    private static void ValidateDistribution(
        IReadOnlyDictionary<string, double> probabilities,
        IReadOnlySet<string> expected)
    {
        if (probabilities.Count != expected.Count
            || probabilities.Keys.Any(key => !expected.Contains(key))
            || Math.Abs(probabilities.Values.Sum() - 1) > 0.000001)
            throw new JsonException("MindsHub returned an incomplete or invalid probability distribution.");
    }

    private static Dictionary<string, double>? ReadProbabilities(JsonElement answer)
    {
        if (!answer.TryGetProperty("probabilities", out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("MindsHub decision probabilities must be an object.");

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var probability = property.Value.ValueKind == JsonValueKind.Number
                ? property.Value.GetDouble()
                : throw new JsonException("MindsHub decision probabilities must be numeric.");
            if (!double.IsFinite(probability) || probability is < 0 or > 1
                || !probabilities.TryAdd(property.Name, probability))
                throw new JsonException("MindsHub returned an invalid decision probability.");
        }
        return probabilities;
    }

    private static double ReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
            throw new JsonException($"MindsHub answer field '{property}' must be a finite number.");
        return number;
    }

    private static int? ReadUsage(JsonElement usage, string property)
    {
        if (!usage.TryGetProperty(property, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var count)
            || count < 0)
            throw new JsonException($"MindsHub usage.{property} must be a nonnegative integer.");
        return count;
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new JsonException($"MindsHub answer field '{property}' must be a string.");

    private string RemoveProviderPrefix(string model)
    {
        var prefix = GetIdentifier() + "/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }
}
