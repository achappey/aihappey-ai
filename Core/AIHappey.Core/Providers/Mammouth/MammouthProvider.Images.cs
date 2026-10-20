using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Mammouth;

public sealed partial class MammouthProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => (await SendImageGenerationAsync(options, cancellationToken)).Response;

    private async Task<(OpenAIImagesResponse Response, JsonElement Metadata, Dictionary<string, string> Headers, string[] Images)> SendImageGenerationAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken)
    {
        options.ValidateOpenAIImageGenerationRequest();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        ApplyAuthHeader();
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in JsonSerializer.SerializeToElement(options, ImageJson).EnumerateObject())
            payload[property.Name] = property.Value.Clone();
        payload.Remove("prompt");
        payload.Remove("response_format");
        payload["model"] = JsonSerializer.SerializeToElement(NativeModel(options.Model));
        payload["messages"] = JsonSerializer.SerializeToElement(new[]
        {
            new { role = "user", content = options.Prompt }
        });
        if (payload.TryGetValue("stream", out var stream) && stream.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("Mammouth does not document streaming image generation.");
        payload["stream"] = JsonSerializer.SerializeToElement(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            throw new JsonException("Mammouth image response must contain chat choices.");
        var images = new List<string>();
        var data = new List<OpenAIImageData>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object ||
                !choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("images", out var nativeImages))
                continue;
            if (nativeImages.ValueKind != JsonValueKind.Array)
                throw new JsonException("Mammouth message images must be an array.");
            foreach (var image in nativeImages.EnumerateArray())
            {
                if (image.ValueKind != JsonValueKind.Object ||
                    !image.TryGetProperty("image_url", out var imageUrl) || imageUrl.ValueKind != JsonValueKind.Object ||
                    !imageUrl.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String)
                    throw new JsonException("Mammouth image must contain image_url.url.");
                var value = url.GetString()!;
                var separator = value.IndexOf(',');
                if (!value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || separator < 0 ||
                    !value[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ||
                    separator == value.Length - 1)
                    throw new JsonException("Mammouth must return inline base64 image data.");
                var base64 = value[(separator + 1)..];
                try
                {
                    if (Convert.FromBase64String(base64).Length == 0)
                        throw new JsonException("Mammouth returned empty image data.");
                }
                catch (FormatException ex)
                {
                    throw new JsonException("Mammouth returned invalid base64 image data.", ex);
                }
                images.Add(value);
                data.Add(options.ResponseFormat == "url"
                    ? new OpenAIImageData { Url = value }
                    : new OpenAIImageData { B64Json = base64 });
            }
        }
        if (images.Count == 0)
            throw new JsonException("Mammouth returned no images in chat choices.");
        var result = new OpenAIImagesResponse
        {
            Created = root.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number &&
                created.TryGetInt64(out var timestamp)
                ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Data = data
        };
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            result.Usage = new()
            {
                InputTokens = ReadTokenCount(usage, "prompt_tokens"),
                OutputTokens = ReadTokenCount(usage, "completion_tokens"),
                TotalTokens = ReadTokenCount(usage, "total_tokens")
            };
        return (result, root.Clone(), response.GetHeaders(), images.ToArray());
    }

    private static int? ReadTokenCount(JsonElement usage, string name)
        => usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt32(out var count) ? count : null;

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Mammouth does not document streaming image generation.");

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Files?.Any() == true || request.Mask is not null || request.AspectRatio is not null)
            throw new NotSupportedException("Use native image options for Mammouth; image editing and aspect-ratio translation are not supported.");
        var native = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var options) == true)
        {
            if (options.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Mammouth provider options must be an object.", nameof(request));
            foreach (var property in options.EnumerateObject())
                native[property.Name] = property.Value.Clone();
        }
        if (request.Seed is not null)
            native["seed"] = JsonSerializer.SerializeToElement(request.Seed);
        native.Remove("model");
        native.Remove("prompt");
        native.Remove("response_format");
        if (request.N is not null) native.Remove("n");
        if (request.Size is not null) native.Remove("size");
        var timestamp = DateTime.UtcNow;
        var (result, metadata, headers, images) = await SendImageGenerationAsync(new()
        {
            Model = request.Model, Prompt = request.Prompt, N = request.N, Size = request.Size,
            ResponseFormat = "b64_json", AdditionalProperties = native
        }, cancellationToken);
        return new ImageResponse
        {
            Images = images,
            Response = new()
            {
                Timestamp = timestamp, ModelId = $"{GetIdentifier()}/{NativeModel(request.Model)}", Headers = headers
            },
            Usage = result.Usage is null ? null : new()
            {
                InputTokens = result.Usage.InputTokens, OutputTokens = result.Usage.OutputTokens,
                TotalTokens = result.Usage.TotalTokens
            },
            ProviderMetadata = new() { [GetIdentifier()] = metadata }
        };
    }
}
