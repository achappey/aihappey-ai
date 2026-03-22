using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TextSynth;

public partial class TextSynthProvider
{
    private async Task<ImageResponse> TextSynthImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var localModel = ExtractProviderLocalModelId(request.Model);
        if (!string.Equals(localModel, ImageBaseModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"TextSynth image model '{request.Model}' is not supported.");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["image_count"] = Math.Clamp(request.N ?? TryGetInt(metadata, "image_count") ?? 1, 1, 4),
            ["seed"] = request.Seed ?? TryGetInt(metadata, "seed"),
            ["negative_prompt"] = TryGetString(metadata, "negative_prompt", "negativePrompt"),
            ["timesteps"] = TryGetInt(metadata, "timesteps"),
            ["guidance_scale"] = TryGetDouble(metadata, "guidance_scale", "guidanceScale"),
            ["strength"] = TryGetDouble(metadata, "strength")
        };

        var (width, height) = ResolveImageSize(request.Size, metadata, warnings);
        payload["width"] = width;
        payload["height"] = height;

        var firstFile = request.Files?.FirstOrDefault();
        if (firstFile is not null)
        {
            if (string.Equals(firstFile.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(firstFile.MediaType, "image/jpg", StringComparison.OrdinalIgnoreCase))
            {
                payload["image"] = firstFile.Data.RemoveDataUrlPrefix();
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "files[0].mediaType", reason = "TextSynth seed image expects JPEG base64." });
            }
        }

        var body = JsonSerializer.Serialize(payload, TextSynthJson);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/engines/{ImageBaseModel}/text_to_image")
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TextSynth text_to_image failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var images = new List<string>();
        if (TryGetPropertyIgnoreCase(root, "images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var imageEl in imagesEl.EnumerateArray())
            {
                if (imageEl.ValueKind != JsonValueKind.Object)
                    continue;

                if (TryGetPropertyIgnoreCase(imageEl, "data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
                {
                    var data = dataEl.GetString();
                    if (!string.IsNullOrWhiteSpace(data))
                        images.Add(data!.ToDataUrl("image/jpeg"));
                }
            }
        }

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static (int width, int height) ResolveImageSize(string? size, JsonElement metadata, List<object> warnings)
    {
        var allowed = new HashSet<int> { 384, 512, 640, 768 };

        if (!string.IsNullOrWhiteSpace(size))
        {
            var normalized = size.Trim().Replace(':', 'x').ToLowerInvariant();
            var parts = normalized.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            {
                if (allowed.Contains(w) && allowed.Contains(h) && w * h <= 393216)
                    return (w, h);

                warnings.Add(new { type = "unsupported", feature = "size", reason = "Allowed values are 384/512/640/768 with width*height <= 393216." });
            }
        }

        var width = TryGetInt(metadata, "width") ?? 512;
        var height = TryGetInt(metadata, "height") ?? 512;

        if (!allowed.Contains(width) || !allowed.Contains(height) || width * height > 393216)
            return (512, 512);

        return (width, height);
    }
}

