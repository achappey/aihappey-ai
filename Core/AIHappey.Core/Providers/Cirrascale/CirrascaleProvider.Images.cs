using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cirrascale;

public partial class CirrascaleProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Mask is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "mask" });
        }

        if (request.Files?.Any() == true)
        {
            warnings.Add(new { type = "unsupported", feature = "files" });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["n"] = request.N,
            ["seed"] = request.Seed,
            ["response_format"] = "b64_json"
        };

        var providerOptions = TryGetProviderOptions(request);
        if (providerOptions is { } options)
        {
            if (options.TryGetProperty("cache_interval", out var cacheInterval)
                && cacheInterval.ValueKind == JsonValueKind.Number
                && cacheInterval.TryGetInt32(out var cacheIntervalValue))
            {
                payload["cache_interval"] = cacheIntervalValue;
            }

            if (options.TryGetProperty("guidance_scale", out var guidanceScale)
                && guidanceScale.ValueKind == JsonValueKind.Number
                && guidanceScale.TryGetDouble(out var guidanceScaleValue))
            {
                payload["guidance_scale"] = guidanceScaleValue;
            }

            if (options.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
            {
                payload["id"] = id.GetString();
            }

            if (options.TryGetProperty("negative_prompt", out var negativePrompt)
                && negativePrompt.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(negativePrompt.GetString()))
            {
                payload["negative_prompt"] = negativePrompt.GetString();
            }

            if (options.TryGetProperty("num_inference_steps", out var numInferenceSteps)
                && numInferenceSteps.ValueKind == JsonValueKind.Number
                && numInferenceSteps.TryGetInt32(out var numInferenceStepsValue))
            {
                payload["num_inference_steps"] = numInferenceStepsValue;
            }

            if (options.TryGetProperty("seed_increment", out var seedIncrement)
                && seedIncrement.ValueKind == JsonValueKind.Number
                && seedIncrement.TryGetInt32(out var seedIncrementValue))
            {
                payload["seed_increment"] = seedIncrementValue;
            }
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);

        using var resp = await _client.PostAsync(
            "v2/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = ExtractB64ImagesAsDataUrls(raw, MediaTypeNames.Image.Png);
        if (images.Count == 0)
            throw new Exception("Cirrascale returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
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

    private static JsonElement? TryGetProviderOptions(ImageRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("cirrascale", out var root))
            return null;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        return root;
    }
}
