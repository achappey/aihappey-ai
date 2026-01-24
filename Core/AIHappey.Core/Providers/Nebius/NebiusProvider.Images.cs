using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Nebius;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Nebius;

public sealed partial class NebiusProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);
        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));
        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));
        var metadata = imageRequest.GetProviderMetadata<NebiusImageProviderMetadata>(GetIdentifier());

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (imageRequest.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Nebius /v1/images/generations does not expose a multi-image parameter in the published schema." });

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        // Nebius expects width/height integers (optional).
        var size = imageRequest.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var width = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageWidth();
        var height = string.IsNullOrWhiteSpace(size) ? null : new ImageRequest { Size = size }.GetImageHeight();

        if ((width is null || height is null) && !string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
        {
            var inferred = imageRequest.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                width ??= inferred.Value.width;
                height ??= inferred.Value.height;
            }
        }

        var responseMimeType = string.IsNullOrEmpty(metadata?.ResponseExtension) ? "image/webp" :
            metadata?.ResponseExtension == "jpg" ? "image/jpeg" : $"image/{metadata?.ResponseExtension}";

        var payload = JsonSerializer.Serialize(new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            width,
            height,
            guidance_scale = metadata?.GuidanceScale,
            response_extension = metadata?.ResponseExtension,
            num_inference_steps = metadata?.NumInferenceSteps,
            negative_prompt = metadata?.NegativePrompt,
            seed = imageRequest.Seed,
            response_format = "b64_json"
        }, ImageJson);

        using var resp = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");
        var images = ExtractB64ImagesAsDataUrls(raw, responseMimeType);
        if (images.Count == 0)
            throw new Exception("Nebius returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private static List<string> ExtractB64ImagesAsDataUrls(string rawJson, string mediaType)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop))
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(mediaType));
        }

        return images;
    }
}

