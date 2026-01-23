using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Zai;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider : IModelProvider
{
    private static readonly JsonSerializerOptions zaiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Z.AI v4 images/generations returns a single image URL at data[0].url.
        // Our gateway contract expects base64 data URLs, so we fetch and convert.

        if (imageRequest.N is not null && imageRequest.N.Value > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n",
                details = "Z.AI image generations currently returns a single image; requested n>1 will be ignored."
            });
        }

        if (imageRequest.Seed.HasValue)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        if (imageRequest.Mask is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "mask"
            });
        }

        if (imageRequest.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files",
                details = "Z.AI image generations endpoint is text-to-image only in this integration."
            });
        }

        // Provider metadata
        var metadata = imageRequest.GetImageProviderMetadata<ZaiImageProviderMetadata>(GetIdentifier());

        // Size selection: prefer explicit size, else derive from aspect_ratio.
        // Z.AI expects size as e.g. "1280x1280".
        var size = !string.IsNullOrWhiteSpace(imageRequest.Size)
            ? imageRequest.Size
            : DeriveZaiSizeFromAspectRatio(imageRequest.AspectRatio);

        // Docs: glm-image default quality is hd; other models default is standard.
        // Our providerMetadata has default "hd"; if user explicitly sets, we forward it.
        // If not provided, omit and let Z.AI apply model defaults.
        string? quality = null;
        if (!string.IsNullOrWhiteSpace(metadata?.Quality))
        {
            var q = metadata!.Quality!.Trim();
            if (string.Equals(q, "hd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(q, "standard", StringComparison.OrdinalIgnoreCase))
            {
                quality = q.ToLowerInvariant();
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = imageRequest.Model,
            ["prompt"] = imageRequest.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(size) ? "1280x1280" : size,
            ["quality"] = quality ?? "hd"
        };

        var json = JsonSerializer.Serialize(payload, zaiJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v4/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Z.AI image generation failed ({(int)resp.StatusCode})"
                : $"Z.AI image generation failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // Extract image URL
        string? imageUrl = null;
        if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            var first = dataArr.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                imageUrl = urlEl.GetString();
        }

        if (string.IsNullOrWhiteSpace(imageUrl))
            throw new InvalidOperationException("Z.AI image generation returned no image url.");

        // Download image bytes
        using var imgResp = await _client.GetAsync(imageUrl, cancellationToken);
        var imgBytes = await imgResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!imgResp.IsSuccessStatusCode || imgBytes is null || imgBytes.Length == 0)
        {
            var reason = await imgResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to download Z.AI image url: {(int)imgResp.StatusCode} {reason}");
        }

        var mediaType = imgResp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            mediaType = "image/png";

        var b64 = Convert.ToBase64String(imgBytes);
        var dataUrl = $"data:{mediaType};base64,{b64}";

        // Preserve structured response data as providerMetadata
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["zai"] = root.Clone()
        };

        return new ImageResponse
        {
            Images = [dataUrl],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = imageRequest.Model,
                Body = raw
            }
        };
    }

    private static string? DeriveZaiSizeFromAspectRatio(string? aspectRatio)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return null;

        // Accept formats like "1:1", "3:2", "2:3".
        var ar = aspectRatio.Trim();
        if (string.Equals(ar, "1:1", StringComparison.OrdinalIgnoreCase)) return "1280x1280";
        if (string.Equals(ar, "3:2", StringComparison.OrdinalIgnoreCase)) return "1568x1056";
        if (string.Equals(ar, "2:3", StringComparison.OrdinalIgnoreCase)) return "1056x1568";

        // Common alternates (best-effort)
        if (string.Equals(ar, "4:3", StringComparison.OrdinalIgnoreCase)) return "1472x1088";
        if (string.Equals(ar, "3:4", StringComparison.OrdinalIgnoreCase)) return "1088x1472";
        if (string.Equals(ar, "16:9", StringComparison.OrdinalIgnoreCase)) return "1728x960";
        if (string.Equals(ar, "9:16", StringComparison.OrdinalIgnoreCase)) return "960x1728";

        return null;
    }

}
