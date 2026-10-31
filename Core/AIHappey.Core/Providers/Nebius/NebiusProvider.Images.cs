using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

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
        var metadata = imageRequest.GetProviderMetadata<JsonElement>(GetIdentifier());
        var additionalMetadata = GetAdditionalMetadata(
            metadata,
            "guidance_scale",
            "response_extension",
            "num_inference_steps",
            "negative_prompt");
        var responseExtension = GetMetadataValue<string>(metadata, "response_extension");
        var guidanceScale = GetMetadataValue<float?>(metadata, "guidance_scale");
        var numInferenceSteps = GetMetadataValue<int?>(metadata, "num_inference_steps");
        var negativePrompt = GetMetadataValue<string>(metadata, "negative_prompt");

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

        var responseMimeType = string.IsNullOrEmpty(responseExtension) ? "image/webp" :
            responseExtension == "jpg" ? "image/jpeg" : $"image/{responseExtension}";

        var payload = additionalMetadata;
        payload["model"] = imageRequest.Model;
        payload["prompt"] = imageRequest.Prompt;
        if (width.HasValue)
            payload["width"] = width.Value;
        if (height.HasValue)
            payload["height"] = height.Value;
        if (imageRequest.Seed.HasValue)
            payload["seed"] = imageRequest.Seed.Value;
        payload["response_format"] = "b64_json";

        if (guidanceScale.HasValue)
            payload["guidance_scale"] = guidanceScale.Value;

        if (responseExtension is not null)
            payload["response_extension"] = responseExtension;

        if (numInferenceSteps.HasValue)
            payload["num_inference_steps"] = numInferenceSteps.Value;

        if (negativePrompt is not null)
            payload["negative_prompt"] = negativePrompt;

        var serializedPayload = JsonSerializer.Serialize(payload, ImageJson);

        using var resp = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(serializedPayload, Encoding.UTF8, MediaTypeNames.Application.Json),
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
                ModelId = imageRequest.Model.ToModelId(GetIdentifier()) 
            }
        };
    }

    private static Dictionary<string, object?> GetAdditionalMetadata(JsonElement metadata, params string[] mappedProperties)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return [];

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Provider metadata must be a JSON object.");

        var payload = new Dictionary<string, object?>();
        foreach (var property in metadata.EnumerateObject())
        {
            if (mappedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static T? GetMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        T? result = default;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                result = property.Value.Deserialize<T>(JsonSerializerOptions.Web);
        }

        return result;
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
