using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var payload = OneInferJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["messages"] = new[]
        {
            new
            {
                role = "user",
                content = request.Prompt
            }
        };

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size;
        if (request.N.HasValue)
            payload["number"] = request.N.Value;
        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "OneInfer image generation uses JSON text-to-image payloads in this adapter." });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask", details = "OneInfer image generation uses JSON text-to-image payloads in this adapter." });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ula/generate-image")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OneInferJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer image generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var data = OneInferGetData(root);
        var images = await ExtractOneInferImagesAsync(data, cancellationToken);

        if (images.Count == 0)
            throw new InvalidOperationException("OneInfer image generation response contained no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Usage = ExtractOneInferImageUsage(data),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadOneInferUnixTimestamp(data, "created") ?? now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<List<string>> ExtractOneInferImagesAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var images = new List<string>();

        if (!data.TryGetProperty("images", out var imagesElement) || imagesElement.ValueKind != JsonValueKind.Array)
            return images;

        foreach (var item in imagesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var mediaType = OneInferImageMediaTypeFromFormat(
                OneInferTryGetString(item, "mime_type", "mimeType", "format", "type"));

            var b64 = OneInferTryGetString(item, "b64_json", "base64", "base64_data", "data");
            if (!string.IsNullOrWhiteSpace(b64))
            {
                images.Add(b64.ToDataUrl(mediaType));
                continue;
            }

            var url = OneInferTryGetString(item, "url", "image_url", "imageUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            images.Add(await NormalizeOneInferImageOutputAsync(url, mediaType, cancellationToken));
        }

        return images;
    }

    private async Task<string> NormalizeOneInferImageOutputAsync(string value, string fallbackMediaType, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
            return value;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var imageResponse = await _client.GetAsync(uri, cancellationToken);
            var bytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!imageResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download OneInfer image from returned URL ({(int)imageResponse.StatusCode}).");

            var mediaType = imageResponse.Content.Headers.ContentType?.MediaType
                ?? OneInferGuessImageMediaType(value)
                ?? fallbackMediaType;

            return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
        }

        return value.ToDataUrl(fallbackMediaType);
    }

    private static ImageUsageData? ExtractOneInferImageUsage(JsonElement data)
    {
        if (!data.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        var result = new ImageUsageData
        {
            InputTokens = OneInferTryGetInt(usage, "prompt_tokens", "input_tokens", "inputTokens"),
            OutputTokens = OneInferTryGetInt(usage, "completion_tokens", "output_tokens", "outputTokens"),
            TotalTokens = OneInferTryGetInt(usage, "total_tokens", "totalTokens")
        };

        return result.InputTokens.HasValue || result.OutputTokens.HasValue || result.TotalTokens.HasValue
            ? result
            : null;
    }
}
