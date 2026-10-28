using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private const int InworldDecisionMaxRequestBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions InworldDecisionJson = new(JsonSerializerDefaults.Web);

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException(
            "Inworld Decisions accepts native noul, choice, and score questions, not OpenAI Decisions requests.");
    }

    private async Task<DecisionResponse> ExecuteInworldDecisionAsync(
        DecisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sdkState = string.Equals(request.StateFormat, "sdk", StringComparison.Ordinal);
        request = request.PrepareForProvider(GetIdentifier());
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerOptions) == true
            && (providerOptions.ValueKind != JsonValueKind.Object || providerOptions.EnumerateObject().Any()))
        {
            throw new NotSupportedException("Inworld Decisions does not document provider-specific request options.");
        }

        var model = StripInworldDecisionProviderPrefix(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var state = BuildInworldDecisionState(request.State, sdkState);
        ValidateInworldDecisionState(state);

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type
            };
            if (question.Instructions is not null)
                mapped["instructions"] = JsonNode.Parse(question.Instructions.Value.GetRawText());

            switch (question)
            {
                case DecisionBooleanQuestion { Criteria: not null } boolean:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, InworldDecisionJson);
                    break;
                case DecisionChoiceQuestion choice:
                    var choiceCriteria = new JsonObject();
                    foreach (var (option, description) in choice.Criteria)
                        choiceCriteria[option] = SerializeInworldDecisionInput(description);
                    mapped["criteria"] = choiceCriteria;
                    break;
                case DecisionScoreQuestion score:
                    var levels = new JsonArray();
                    foreach (var level in score.Criteria)
                    {
                        if (level is null)
                            throw new ArgumentException($"Inworld score question '{id}' requires non-null level descriptions.", nameof(request));
                        levels.Add(SerializeInworldDecisionInput(level));
                    }
                    mapped["criteria"] = levels;
                    break;
            }

            questions[id] = mapped;
        }

        var payload = new JsonObject
        {
            ["model"] = model,
            ["state"] = JsonNode.Parse(state.GetRawText()),
            ["questions"] = questions
        };
        using var content = new StringContent(payload.ToJsonString(InworldDecisionJson), Encoding.UTF8, MediaTypeNames.Application.Json);
        await content.LoadIntoBufferAsync(cancellationToken);
        if (content.Headers.ContentLength is > InworldDecisionMaxRequestBytes)
            throw new ArgumentException("Inworld Decisions requests must not exceed 10 MiB.", nameof(request));

        using var message = new HttpRequestMessage(HttpMethod.Post, "alpha/decisions")
        {
            Content = content
        };
        foreach (var (name, value) in request.Headers?.GetProviderPassthroughHeaders(GetIdentifier()) ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Inworld Decisions request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapInworldDecisionResponse(body, request);
            var headers = response.GetHeaders();
            var responseModel = body.GetProperty("model").GetString()!;
            var generationId = body.TryGetProperty("metadata", out var metadata)
                ? ReadInworldDecisionString(metadata, "generation_id")
                : null;

            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new DecisionResponseMetadata
            {
                Id = generationId ?? headers.GetValueOrDefault("x-request-id"),
                Timestamp = DateTimeOffset.UtcNow,
                ModelId = responseModel.ToModelId(GetIdentifier()),
                Headers = headers.ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.OrdinalIgnoreCase),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Inworld returned an invalid Decisions response.", exception);
        }
    }

    private static JsonNode? SerializeInworldDecisionInput(DecisionInput? input)
        => input is null ? null : JsonNode.Parse(input.Value.GetRawText());

    private static string StripInworldDecisionProviderPrefix(string model)
    {
        const string prefix = "inworld/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model[prefix.Length..]
            : model;
    }

    private static JsonElement BuildInworldDecisionState(DecisionInput state, bool sdkState)
    {
        if (!sdkState)
            return state.Value.Clone();

        var content = new JsonArray();
        var messages = state.Value;
        foreach (var message in messages.EnumerateArray())
        {
            foreach (var part in message.GetProperty("content").EnumerateArray())
            {
                switch (ReadInworldDecisionString(part, "type"))
                {
                    case "input_text":
                        content.Add(JsonValue.Create(ReadInworldDecisionString(part, "text")));
                        break;
                    case "input_image":
                        content.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = ReadInworldDecisionString(part, "image_url")
                            }
                        });
                        break;
                    default:
                        throw new NotSupportedException("Inworld Decisions supports SDK text and inline image state parts only.");
                }
            }
        }

        return JsonSerializer.SerializeToElement(content, InworldDecisionJson);
    }

    private static void ValidateInworldDecisionState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Array)
            return;

        foreach (var part in state.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (type.GetString())
            {
                case "audio":
                case "input_audio":
                case "video":
                case "input_video":
                    throw new NotSupportedException("Inworld Decisions does not support audio or video state input.");
                case "image_url":
                    ValidateInworldDecisionImage(part);
                    break;
            }
        }
    }

    private static void ValidateInworldDecisionImage(JsonElement part)
    {
        if (part.EnumerateObject().Any(property => property.Name is not ("type" or "image_url"))
            || !part.TryGetProperty("image_url", out var image)
            || image.ValueKind != JsonValueKind.Object
            || image.EnumerateObject().Any(property => property.Name is not ("url" or "detail"))
            || !image.TryGetProperty("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("Inworld decision image parts require an image_url object containing an inline data URL.");
        }

        var url = urlElement.GetString()!;
        var comma = url.IndexOf(',');
        if (comma < 0 || !new[]
            {
                "data:image/png;base64",
                "data:image/jpeg;base64",
                "data:image/webp;base64"
            }.Contains(url[..comma], StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Inworld Decisions accepts only inline base64 PNG, JPEG, or WebP images; remote image URLs are unsupported.");
        }

        if (image.TryGetProperty("detail", out var detail)
            && (detail.ValueKind != JsonValueKind.String
                || !new[] { "auto", "low", "high" }.Contains(detail.GetString(), StringComparer.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Inworld decision image detail must be 'auto', 'low', or 'high'.");
        }

        try
        {
            if (Convert.FromBase64String(url[(comma + 1)..]).Length == 0)
                throw new FormatException();
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Inworld decision image data must contain standard base64 bytes.", exception);
        }
    }

    private static DecisionResponse MapInworldDecisionResponse(JsonElement body, DecisionRequest request)
    {
        if (body.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(ReadInworldDecisionString(body, "model")))
            throw new JsonException("Inworld Decisions response requires a model.");

        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object
            || nativeAnswers.EnumerateObject().Count() != request.Questions.Count
            || nativeAnswers.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != request.Questions.Count)
            throw new JsonException("Inworld Decisions answers must cover every question exactly once.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            var answer = nativeAnswers.GetProperty(id);
            var expectedType = question is DecisionBooleanQuestion ? "noul" : question.Type;
            if (ReadInworldDecisionString(answer, "type") != expectedType)
                throw new JsonException($"Inworld returned an unexpected answer type for '{id}'.");

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer
                    {
                        Probability = ReadInworldDecisionProbability(answer.GetProperty("noul"))
                    };
                    break;
                case DecisionChoiceQuestion choice:
                    ReadInworldDecisionProbability(answer.GetProperty("confidence"));
                    var selected = ReadInworldDecisionString(answer, "choice");
                    if (!choice.Criteria.ContainsKey(selected))
                        throw new JsonException($"Inworld returned an unknown choice for '{id}'.");
                    var choiceProbabilities = ReadInworldDecisionDistribution(
                        answer.GetProperty("probabilities"),
                        choice.Criteria.Keys);
                    if (choiceProbabilities[selected] + 1e-6 < choiceProbabilities.Values.Max())
                        throw new JsonException($"Inworld selected a non-maximal choice for '{id}'.");
                    answers[id] = new DecisionChoiceAnswer { Choice = selected, Probabilities = choiceProbabilities };
                    break;
                case DecisionScoreQuestion score:
                    ReadInworldDecisionProbability(answer.GetProperty("confidence"));
                    var keys = Enumerable.Range(0, score.Criteria.Count)
                        .Select(index => index.ToString(CultureInfo.InvariantCulture))
                        .ToArray();
                    var scoreProbabilities = ReadInworldDecisionDistribution(
                        answer.GetProperty("probabilities"),
                        keys);
                    ValidateInworldDecisionLegend(answer.GetProperty("legend"), keys);
                    var value = ReadInworldDecisionNumber(answer.GetProperty("score"));
                    var expectedScore = scoreProbabilities.Sum(item =>
                        int.Parse(item.Key, CultureInfo.InvariantCulture) * item.Value);
                    if (value < 0 || value > keys.Length - 1 || Math.Abs(value - expectedScore) > 1e-6)
                        throw new JsonException($"Inworld returned an invalid weighted score for '{id}'.");
                    answers[id] = new DecisionScoreAnswer { Score = value, Probabilities = scoreProbabilities };
                    break;
                default:
                    throw new JsonException("Inworld returned an unsupported decision question type.");
            }
        }

        var usage = body.GetProperty("usage");
        var inputTokens = usage.GetProperty("input_tokens").GetInt32();
        var outputTokens = usage.GetProperty("output_tokens").GetInt32();
        if (inputTokens < 0 || outputTokens < 0)
            throw new JsonException("Inworld returned invalid Decisions usage.");

        return new DecisionResponse
        {
            Answers = answers,
            Usage = new DecisionUsage { InputTokens = inputTokens, OutputTokens = outputTokens }
        };
    }

    private static void ValidateInworldDecisionLegend(JsonElement legend, IReadOnlyCollection<string> keys)
    {
        if (legend.ValueKind != JsonValueKind.Object
            || legend.EnumerateObject().Count() != keys.Count
            || keys.Any(key => !legend.TryGetProperty(key, out var value)
                || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)))
        {
            throw new JsonException("Inworld returned an invalid score legend.");
        }
    }

    private static Dictionary<string, double> ReadInworldDecisionDistribution(
        JsonElement value,
        IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Inworld decision probabilities must be an object.");

        var distribution = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name)
                || !distribution.TryAdd(property.Name, ReadInworldDecisionProbability(property.Value)))
            {
                throw new JsonException("Inworld returned an unknown or duplicate decision probability key.");
            }
        }

        if (distribution.Count != expected.Count || Math.Abs(distribution.Values.Sum() - 1) > 1e-6)
            throw new JsonException("Inworld returned an incomplete or unnormalized probability distribution.");
        return distribution;
    }

    private static double ReadInworldDecisionProbability(JsonElement value)
    {
        var number = ReadInworldDecisionNumber(value);
        if (number is < 0 or > 1)
            throw new JsonException("Inworld decision probabilities must be in [0, 1].");
        return number;
    }

    private static double ReadInworldDecisionNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            throw new JsonException("Inworld decision values must be finite numbers.");
        }

        return number;
    }

    private static string ReadInworldDecisionString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JsonException($"Inworld decision response requires string '{propertyName}'.");
}
