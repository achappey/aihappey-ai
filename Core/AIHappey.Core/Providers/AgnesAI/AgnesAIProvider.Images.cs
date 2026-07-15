using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AgnesAI;

public partial class AgnesAIProvider
{
    private async Task<ImageResponse> AgnesImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Agnes image generation docs do not define a generic image count parameter." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = CreateAgnesPayload(
            metadata,
            "tags",
            "image",
            "images",
            "image_url",
            "imageUrl",
            "image_urls",
            "imageUrls",
            "extra_body",
            "extraBody",
            "response_format",
            "responseFormat");

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        var size = ResolveAgnesImageSize(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        var extraBody = CreateAgnesExtraBody(metadata, "image", "images", "image_urls", "imageUrls", "response_format", "responseFormat");
        extraBody["response_format"] = ResolveAgnesImageResponseFormat(metadata, warnings);

        var imageUrls = ResolveAgnesImageInputUrls(request, metadata, warnings);
        if (imageUrls.Count > 0)
            extraBody["image"] = imageUrls;

        if (extraBody.Count > 0)
            payload["extra_body"] = extraBody;

        var tags = ResolveAgnesTags(metadata, includeImg2Img: imageUrls.Count > 0);
        if (tags.Count > 0)
            payload["tags"] = tags;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AgnesJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agnes image request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var outputUrls = ExtractAgnesImageOutputUrls(root);

        if (outputUrls.Count == 0)
            throw new InvalidOperationException("Agnes image response missing 'data[].url'.");

        var images = new List<string>(outputUrls.Count);
        foreach (var outputUrl in outputUrls)
        {
            var (bytes, mediaType) = await DownloadAgnesBinaryAsync(
                outputUrl,
                GuessAgnesImageMediaType(outputUrl) ?? MediaTypeNames.Image.Png,
                cancellationToken);

            images.Add(Convert.ToBase64String(bytes).ToDataUrl(mediaType));
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }
}
