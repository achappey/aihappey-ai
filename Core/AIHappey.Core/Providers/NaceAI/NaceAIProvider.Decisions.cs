using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NaceAI;

/// <summary>Native POST /systemone (noul, choice and score) behind the gateway's Vercel decisions contract.</summary>
public sealed partial class NaceAIProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxQuestions = 512;
    private const int MaxImages = 8;
    private const int MaxImageBytes = 10 * 1024 * 1024;

    // Only drex-max documents image input; the Drex text models answer 422 to an images array.
    private static bool IsTextOnlyDecisionModel(string? model)
        => !string.Equals(NormalizeModelId(model), "drex-max", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModelId(string? model)
    {
        var value = model ?? string.Empty;
        return value.StartsWith("naceai/", StringComparison.OrdinalIgnoreCase) ? value["naceai/".Length..] : value;
    }

    public async Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyAuthHeader();
        request = DecisionRequestCompatibility.PrepareForProvider(request, GetIdentifier());

        var model = NormalizeModelId(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.State is null)
            throw new ArgumentException("Decision state is required.", nameof(request));
        if (request.Questions is null || request.Questions.Count is < 1 or > MaxQuestions)
            throw new ArgumentException($"NaceAI supports 1 to {MaxQuestions} decision questions.", nameof(request));
        if (request.MaxOutputTokens is not null || request.Reasoning is not null)
            throw new ArgumentException("NaceAI Decisions does not support reasoning or output-token controls.", nameof(request));

        ValidateProviderOptions(request.ProviderOptions);
        var warnings = new List<object>();
        var questions = new JsonObject();
        foreach (var (id, question) in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(id) || question?.Instructions is null)
                throw new ArgumentException("Questions require a nonempty ID and instructions.", nameof(request));

            var mapped = new JsonObject
            {
                ["type"] = question.Type == "boolean" ? "noul" : question.Type,
                ["instructions"] = JsonNode.Parse(question.Instructions.Value.GetRawText())
            };

            switch (question)
            {
                case DecisionBooleanQuestion boolean:
                    if (boolean.Criteria is not null)
                        mapped["criteria"] = JsonSerializer.SerializeToNode(boolean.Criteria, JsonOptions);
                    break;
                case DecisionChoiceQuestion { Criteria.Count: >= 2 } choice:
                    mapped["criteria"] = JsonSerializer.SerializeToNode(choice.Criteria, JsonOptions);
                    break;
                case DecisionScoreQuestion { Criteria.Count: >= 2 } score:
                    if (score.Criteria.Any(level => level is null))
                        throw new ArgumentException("NaceAI score levels cannot be null.", nameof(request));
                    mapped["criteria"] = JsonSerializer.SerializeToNode(score.Criteria, JsonOptions);
                    break;
                default:
                    throw new ArgumentException(
                        $"Question '{id}' is not supported by NaceAI Drex; boolean, choice and score questions with criteria are supported.",
                        nameof(request));
            }

            questions[id] = mapped;
        }

        var (state, images) = MapState(request.State.Value);
        ValidateImages(model, images);

        var payload = new JsonObject { ["model"] = model, ["state"] = state };
        if (images.Count > 0)
            payload["images"] = new JsonArray(images.Select(image => (JsonNode?)JsonValue.Create(image)).ToArray());
        payload["questions"] = questions;

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/systemone")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(payload.ToJsonString(JsonOptions)))
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
            result.Warnings = warnings;
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
            throw new InvalidOperationException("NaceAI returned an invalid decision response.", exception);
        }
    }

    private static HttpRequestException CreateDecisionException(
        HttpResponseMessage response,
        IReadOnlyDictionary<string, string> headers,
        string raw)
    {
        var requestId = headers.GetValueOrDefault("x-request-id");
        var retryAfter = headers.GetValueOrDefault("Retry-After");
        return new HttpRequestException(
            $"NaceAI decision request failed ({(int)response.StatusCode} {response.ReasonPhrase})"
            + (requestId is null ? "" : $" [request {requestId}]")
            + (retryAfter is null ? "" : $" [Retry-After: {retryAfter}]")
            + $": {raw}",
            null,
            response.StatusCode);
    }

    private static void ValidateProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("naceai", out var options) != true || options.ValueKind == JsonValueKind.Null)
            return;
        if (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())
            throw new ArgumentException("NaceAI Decisions supports no additional provider options.");
    }

    private static (JsonNode? State, List<string> Images) MapState(JsonElement state)
    {
        var images = new List<string>();
        var texts = new List<string>();

        if (state.ValueKind == JsonValueKind.Array && state.GetArrayLength() > 0
            && state.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("role", out var role) && role.GetString() == "user"))
        {
            foreach (var message in state.EnumerateArray())
            {
                if (!message.TryGetProperty("content", out var content))
                    throw new ArgumentException("Decision messages require content.");
                if (content.ValueKind == JsonValueKind.String)
                    texts.Add(content.GetString() ?? string.Empty);
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var part in content.EnumerateArray())
                        CollectPart(part, texts, images);
                else
                    throw new ArgumentException("Decision message content must contain text or image parts.");
            }
        }
        else if (state.ValueKind == JsonValueKind.Object && IsPart(state))
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
            throw new ArgumentException("NaceAI Decisions requires text state alongside images.");

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
                throw new ArgumentException("Unsupported decision content part.");
        }
    }

    private static void ValidateImages(string model, IReadOnlyCollection<string> images)
    {
        if (images.Count == 0)
            return;
        if (IsTextOnlyDecisionModel(model))
            throw new ArgumentException("Only drex-max accepts images; the other Drex models are text only.");
        if (images.Count > MaxImages)
            throw new ArgumentException($"NaceAI Decisions accepts at most {MaxImages} images.");

        foreach (var image in images)
        {
            var comma = image.IndexOf(',');
            if (comma < 0 || image[..comma].ToLowerInvariant() is not
                ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/webp;base64"))
                throw new ArgumentException("NaceAI Decisions images must be base64 PNG, JPEG or WebP data URLs; image links are not supported.");

            // Base64 inflates by 4/3; reject before decoding very large payloads.
            if ((image.Length - comma - 1) / 4L * 3 > MaxImageBytes)
                throw new ArgumentException("NaceAI Decisions images must be at most 10 MB.");
            try
            {
                if (Convert.FromBase64String(image[(comma + 1)..]).Length is 0 or > MaxImageBytes)
                    throw new ArgumentException("NaceAI Decisions images must be non-empty and at most 10 MB.");
            }
            catch (FormatException exception)
            {
                throw new ArgumentException("NaceAI Decisions image contains invalid base64.", exception);
            }
        }
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

            switch (question)
            {
                case DecisionBooleanQuestion:
                    answers[id] = new DecisionBooleanAnswer { Probability = ReadProbability(answer.GetProperty("noul")) };
                    break;
                case DecisionChoiceQuestion choice:
                    var selected = answer.GetProperty("choice").GetString() ?? throw new JsonException("Missing choice.");
                    answers[id] = new DecisionChoiceAnswer
                    {
                        Choice = selected,
                        Probabilities = ReadDistribution(answer.GetProperty("probabilities"), choice.Criteria.Keys)
                    };
                    break;
                case DecisionScoreQuestion score:
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
                    throw new JsonException("Unsupported question type.");
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
