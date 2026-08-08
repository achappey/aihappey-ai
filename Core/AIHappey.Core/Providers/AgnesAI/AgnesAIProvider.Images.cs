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
            "image",
            "images",
            "image_url",
            "imageUrl",
            "image_urls",
            "imageUrls",
            "extra_body",
            "extraBody",
            "return_base64",
            "returnBase64",
            "response_format",
            "responseFormat");

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        var size = ResolveAgnesImageSize(request, metadata, warnings);
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        var ratio = ResolveAgnesImageRatio(request, metadata);
        if (!string.IsNullOrWhiteSpace(ratio))
            payload["ratio"] = ratio;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        // Agnes supports a top-level Base64 preference for generation and
        // extra_body.response_format for all workflows. Request both so edits
        // and generations consistently avoid an additional download when possible.
        payload["return_base64"] = true;

        var extraBody = CreateAgnesExtraBody(metadata, "image", "images", "image_urls", "imageUrls", "response_format", "responseFormat");
        extraBody["response_format"] = "b64_json";

        var imageUrls = ResolveAgnesImageInputUrls(request, metadata, warnings);
        if (imageUrls.Count > 0)
            extraBody["image"] = imageUrls;

        if (extraBody.Count > 0)
            payload["extra_body"] = extraBody;

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
        var outputs = ExtractAgnesImageOutputs(root);

        if (outputs.Count == 0)
            throw new InvalidOperationException("Agnes image response missing 'data[].b64_json' or 'data[].url'.");

        var images = new List<string>(outputs.Count);
        foreach (var output in outputs)
        {
            if (!string.IsNullOrWhiteSpace(output.Base64))
            {
                images.Add(output.Base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? output.Base64
                    : output.Base64.ToDataUrl(MediaTypeNames.Image.Png));
                continue;
            }

            var (bytes, mediaType) = await DownloadAgnesBinaryAsync(
                output.Url!,
                GuessAgnesImageMediaType(output.Url) ?? MediaTypeNames.Image.Png,
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
