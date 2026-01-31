using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Euqai;

public partial class EuqaiProvider
{
    private static readonly JsonSerializerOptions EuqaiImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<ImageResponse> ImageRequestInternal(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Euqai image generation via chat/completions does not support image inputs in this integration. Ignored files."
            });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (request.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (request.N is not null && request.N.Value > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Euqai chat/completions image generation returns a single image per request. Requested n>1 will be ignored."
            });
        }

        var size = request.Size?.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var hasSize = !string.IsNullOrWhiteSpace(size);

        if (!hasSize && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var inferred = request.AspectRatio.InferSizeFromAspectRatio();
            if (inferred is not null)
            {
                size = $"{inferred.Value.width}x{inferred.Value.height}";
                hasSize = true;
            }
        }

        if (!hasSize)
            size = "1024x1024";

        if (!string.IsNullOrWhiteSpace(request.Size) && !hasSize)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "size",
                details = $"Requested size {request.Size} not supported. Used default settings."
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["size"] = size,
            ["response_format"] = "b64_json"
        };

        var json = JsonSerializer.Serialize(payload, EuqaiImageJson);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Euqai image generation failed: {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = ExtractB64ImagesAsDataUrls(doc.RootElement);

        if (images.Count == 0)
            throw new Exception("Euqai image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static List<string> ExtractB64ImagesAsDataUrls(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64Prop) || b64Prop.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64Prop.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(MediaTypeNames.Image.Png));
        }

        return images;
    }
}
