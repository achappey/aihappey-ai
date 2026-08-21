using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ModelMax;

public partial class ModelMaxProvider
{
    private static readonly JsonSerializerOptions ModelMaxImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Files?.Any() == true || request.Mask is not null)
            throw new NotSupportedException("ModelMax does not document image edits; only image generation is supported.");

        var warnings = new List<object>();
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "ModelMax image generation expects size." });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyModelMaxJsonObject(metadata);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.N is not null)
            payload["n"] = request.N.Value;
        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ModelMaxImageJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModelMax image generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var images = await ExtractModelMaxImagesAsync(root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("ModelMax image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractModelMaxImageUsage(root),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadModelMaxCreated(root) ?? DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options,
        CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        return response.ToOpenAIImagesResponse(options);
    }

    public async IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAIImageGenerationRequest();
        var response = await ImageRequest(options.ToImageRequest(options.Model, GetIdentifier()), cancellationToken);
        foreach (var streamEvent in response.ToOpenAIImageGenerationCompletedEvents(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return streamEvent;
        }
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ModelMax does not document an image edit endpoint.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ModelMax does not document an image edit endpoint.");

    private async Task<List<string>> ExtractModelMaxImagesAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var images = new List<string>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("b64_json", out var base64Element) && base64Element.ValueKind == JsonValueKind.String)
            {
                var base64 = base64Element.GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                    images.Add(base64.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            if (!item.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                continue;

            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(url);
                continue;
            }

            using var imageResponse = await _client.GetAsync(url, cancellationToken);
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download a ModelMax image ({(int)imageResponse.StatusCode}).");

            var mediaType = imageResponse.Content.Headers.ContentType?.MediaType ?? GuessModelMaxImageMediaType(url) ?? MediaTypeNames.Image.Png;
            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        return images;
    }

    private static Dictionary<string, object?> CopyModelMaxJsonObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in element.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private static ImageUsageData? ExtractModelMaxImageUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;
        var result = new ImageUsageData
        {
            InputTokens = TryGetModelMaxInt(usage, "prompt_tokens", "input_tokens"),
            OutputTokens = TryGetModelMaxInt(usage, "completion_tokens", "output_tokens"),
            TotalTokens = TryGetModelMaxInt(usage, "total_tokens")
        };
        return result.InputTokens is not null || result.OutputTokens is not null || result.TotalTokens is not null ? result : null;
    }

    private static int? TryGetModelMaxInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
                return number;
        return null;
    }

    private static DateTime? ReadModelMaxCreated(JsonElement root)
        => root.TryGetProperty("created", out var created) && created.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    private static string? GuessModelMaxImageMediaType(string url)
    {
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;
        return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? MediaTypeNames.Image.Png : null;
    }
}
