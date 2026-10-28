using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SubQ;

/// <summary>Native POST /v1/systemone (noul, choice and score questions) behind the gateway's decisions contracts.</summary>
public sealed partial class SubQProvider
{
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);
    private const string DecisionPath = "systemone";
    private const int MaxQuestions = 128;
    private const int MaxQuestionNameLength = 100;
    private const int MaxImages = 4;
    private const int MaxImageBytes = 4 * 1024 * 1024;
    private const int MaxTotalImageBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> ImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp"
    };

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        request = request.PrepareForProvider(GetIdentifier());

        var model = StripProviderPrefix(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (!IsDecisionModel(model))
            throw new NotSupportedException($"SubQ model '{model}' is not a decision model.");
        if (request.State is null)
            throw new ArgumentException("Decision state is required.", nameof(request));
        if (request.Questions is null || request.Questions.Count is < 1 or > MaxQuestions)
            throw new ArgumentException($"SubQ supports 1 to {MaxQuestions} decision questions.", nameof(request));
        ValidateProviderOptions(request.ProviderOptions);

        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxQuestionNameLength || question?.Instructions is null)
                throw new ArgumentException(
                    $"Questions require instructions and a nonempty ID of at most {MaxQuestionNameLength} characters.",
                    nameof(request));

            var mapped = new JsonObject
            {
                ["type"] = question is DecisionBooleanQuestion ? "noul" : question.Type,
                ["instructions"] = JsonNode.Parse(question.Instructions.Value.GetRawText())
            };

            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, DecisionJsonOptions);
                    break;
                case DecisionChoiceQuestion { Criteria.Count: >= 2 and <= 255 } choice:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, DecisionJsonOptions);
                    break;
                case DecisionScoreQuestion { Criteria.Count: >= 2 and <= 10 } score:
                    if (score.Criteria.Any(level => level is null))
                        throw new ArgumentException("SubQ score levels cannot be null.", nameof(request));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, DecisionJsonOptions);
                    break;
                default:
                    throw new ArgumentException(
                        $"Question '{id}' has unsupported criteria; choices require 2-255 options and scores 2-10 levels.",
                        nameof(request));
            }

            questions[id] = mapped;
        }

        var (state, images) = MapDecisionState(request.State.Value);
        var nativeImages = ValidateAndMapImages(images);

        var payload = new JsonObject { ["model"] = model, ["state"] = state };
        if (nativeImages.Count > 0)
            payload["images"] = nativeImages;
        payload["questions"] = questions;

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, DecisionPath)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString(DecisionJsonOptions)))
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var headers = response.GetHeaders();
        if (!response.IsSuccessStatusCode)
            throw CreateDecisionException(response, headers, raw);

        try
        {
            using var document = JsonDocument.Parse(raw);
            var body = document.RootElement;
            var result = MapDecisionResponse(body, request);
            result.ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(body.Clone());
            result.Response = new()
            {
                ModelId = model.ToModelId(GetIdentifier()),
                Timestamp = DateTimeOffset.UtcNow,
                Id = headers.GetValueOrDefault("x-request-id"),
                Headers = headers.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
                Body = body.Clone()
            };
            return result;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("SubQ returned an invalid decision response.", exception);
        }
    }

    private static void ValidateProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("subq", out var options) != true || options.ValueKind == JsonValueKind.Null)
            return;
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.subq must be an object.");
        if (options.EnumerateObject().Any())
            throw new NotSupportedException("SubQ Decisions does not support provider options.");
    }

    // Errors use {"detail":{"error_type":...,"message":...}}; 402 and 403 are credit and model-access failures.
    private static HttpRequestException CreateDecisionException(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> headers,
        string raw)
    {
        string detail = raw;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("detail", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var type = error.TryGetProperty("error_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (type is not null || text is not null)
                    detail = $"{type}: {text}";
            }
        }
        catch (JsonException)
        {
        }

        var requestId = headers.GetValueOrDefault("x-request-id");
        var retryAfter = headers.GetValueOrDefault("Retry-After");
        return new HttpRequestException(
            $"SubQ decision request failed ({(int)response.StatusCode} {response.ReasonPhrase})"
            + (requestId is null ? "" : $" [request {requestId}]")
            + (retryAfter is null ? "" : $" [Retry-After: {retryAfter}]")
            + $": {detail}",
            null,
            response.StatusCode);
    }

    private static (JsonNode? State, List<string> Images) MapDecisionState(JsonElement state)
    {
        var images = new List<string>();
        var texts = new List<string>();

        if (state.ValueKind == JsonValueKind.Array && state.GetArrayLength() > 0
            && state.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("role", out var role) && role.GetString() == "user"
                && item.TryGetProperty("content", out var content)
                && (content.ValueKind == JsonValueKind.String || content.ValueKind == JsonValueKind.Array)))
        {
            foreach (var message in state.EnumerateArray())
            {
                var content = message.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String)
                    texts.Add(content.GetString() ?? string.Empty);
                else
                    foreach (var part in content.EnumerateArray())
                        CollectPart(part, texts, images);
            }
        }
        else if (IsPart(state))
            CollectPart(state, texts, images);
        else if (state.ValueKind == JsonValueKind.Array && state.EnumerateArray().Any(IsPart))
        {
            // Mixed arrays keep non-part evidence as JSON text so no content is dropped.
            foreach (var part in state.EnumerateArray())
            {
                if (IsPart(part))
                    CollectPart(part, texts, images);
                else
                    texts.Add(part.ValueKind == JsonValueKind.String ? part.GetString() ?? string.Empty : part.GetRawText());
            }
        }
        else
            return (JsonNode.Parse(state.GetRawText()), images);

        if (texts.Count == 0 || texts.All(string.IsNullOrWhiteSpace))
            throw new ArgumentException("SubQ Decisions requires text state alongside images.");

        return (JsonValue.Create(string.Join("\n\n", texts)), images);
    }

    private static bool IsPart(JsonElement value)
        => value.ValueKind == JsonValueKind.Object
           && value.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && type.GetString() is "input_text" or "input_image" or "image_url";

    private static void CollectPart(JsonElement part, List<string> texts, List<string> images)
    {
        if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type))
            throw new ArgumentException("Unsupported decision content part.");

        switch (type.GetString())
        {
            case "input_text" when part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String:
                texts.Add(text.GetString()!);
                break;
            case "input_image" when part.TryGetProperty("image_url", out var url) && url.ValueKind == JsonValueKind.String:
                images.Add(url.GetString()!);
                break;
            case "image_url" when part.TryGetProperty("image_url", out var nested)
                                  && nested.ValueKind == JsonValueKind.Object
                                  && nested.TryGetProperty("url", out var nestedUrl)
                                  && nestedUrl.ValueKind == JsonValueKind.String:
                images.Add(nestedUrl.GetString()!);
                break;
            default:
                throw new ArgumentException("SubQ Decisions supports text and image parts only.");
        }
    }

    // SubQ takes {mediaType, base64}; URLs and data: prefixes are rejected natively, so data URLs are split here.
    private static JsonArray ValidateAndMapImages(IReadOnlyCollection<string> images)
    {
        var result = new JsonArray();
        if (images.Count == 0)
            return result;
        if (images.Count > MaxImages)
            throw new ArgumentException($"SubQ Decisions accepts at most {MaxImages} images.");

        long total = 0;
        foreach (var image in images)
        {
            var comma = image.IndexOf(',');
            const string prefix = "data:";
            const string suffix = ";base64";
            if (comma < 0 || !image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !image[..comma].EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("SubQ Decisions images must be base64 PNG, JPEG or WebP data URLs; image links are not supported.");

            var mediaType = image[prefix.Length..(comma - suffix.Length)].ToLowerInvariant();
            if (!ImageMediaTypes.Contains(mediaType))
                throw new ArgumentException("SubQ Decisions images must be image/png, image/jpeg or image/webp.");

            var base64 = image[(comma + 1)..].Trim();
            // Base64 inflates by 4/3; reject before decoding very large payloads.
            if (base64.Length / 4L * 3 > MaxImageBytes + 3)
                throw new ArgumentException("SubQ Decisions images must be at most 4 MiB decoded.");

            int length;
            try
            {
                length = Convert.FromBase64String(base64).Length;
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("SubQ Decisions image contains invalid base64.", exception);
            }

            if (length is 0 or > MaxImageBytes)
                throw new ArgumentException("SubQ Decisions images must be non-empty and at most 4 MiB decoded.");
            total += length;
            if (total > MaxTotalImageBytes)
                throw new ArgumentException("SubQ Decisions images must total at most 8 MiB decoded.");

            result.Add(new JsonObject { ["mediaType"] = mediaType, ["base64"] = base64 });
        }

        return result;
    }

    private static DecisionResponse MapDecisionResponse(JsonElement body, DecisionRequest request)
    {
        var nativeAnswers = body.GetProperty("answers");
        if (nativeAnswers.ValueKind != JsonValueKind.Object)
            throw new JsonException("Answers must be an object.");

        var answers = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (id, question) in request.Questions)
        {
            if (!nativeAnswers.TryGetProperty(id, out var answer))
                throw new JsonException($"Missing answer for question '{id}'.");

            var type = answer.GetProperty("type").GetString();
            switch (question)
            {
                case DecisionBooleanQuestion when type == "noul":
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice when type == "choice":
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    answers[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys)
                    };
                    break;
                case DecisionScoreQuestion score when type == "score":
                    var keys = Enumerable.Range(0, score.Criteria.Count)
                        .Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();
                    var value = answer.GetProperty("score").GetDouble();
                    if (!double.IsFinite(value) || value < 0 || value > keys.Length - 1)
                        throw new JsonException("Score must be a level index within the submitted rubric.");
                    answers[id] = new DecisionScoreAnswer
                    {
                        Score = value,
                        Probabilities = ReadDistribution(answer.GetProperty("probabilities"), keys)
                    };
                    break;
                default:
                    throw new JsonException("Answer type does not match question.");
            }
        }

        var usage = body.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
            ? usageElement
            : default;
        return new()
        {
            Answers = answers,
            Usage = new()
            {
                InputTokens = ReadCount(usage, "input_tokens"),
                OutputTokens = ReadCount(usage, "output_tokens")
            }
        };
    }

    private static int ReadCount(JsonElement usage, string name)
        => usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var value)
           && value.TryGetInt32(out var count) && count >= 0
            ? count
            : 0;

    private static double ReadProbability(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number is < 0 or > 1)
            throw new JsonException("Probability must be finite and in [0, 1].");
        return number;
    }

    private static Dictionary<string, double> ReadDistribution(JsonElement value, IEnumerable<string> expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected a probability map.");

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !result.TryAdd(property.Name, ReadProbability(property.Value)))
                throw new JsonException("Unknown or duplicate probability key.");
        }

        if (result.Count != expected.Count)
            throw new JsonException("Incomplete probability distribution.");
        return result;
    }
}
