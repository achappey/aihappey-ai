using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Infomaniak;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
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

        var isPhotoMaker = IsPhotoMakerModel(request.Model);

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!isPhotoMaker && request.Files?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "files"
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

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "aspectRatio"
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

        var metadata = request.GetProviderMetadata<InfomaniakImageProviderMetadata>(GetIdentifier());
        var productId = await GetProductIdAsync(cancellationToken);

        var files = request.Files ?? [];
        var sourceImages = files
            .Select(f => f.Data)
            .Where(data => !string.IsNullOrWhiteSpace(data))
            .ToList();

        if (isPhotoMaker && sourceImages.Count == 0)
            throw new ArgumentException("At least one file is required for photomaker model.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["response_format"] = "b64_json"
        };

        if (!isPhotoMaker)
            payload["model"] = request.Model;

        if (isPhotoMaker)
            payload["images"] = sourceImages;

        if (request.N.HasValue)
            payload["n"] = request.N.Value;

        if (!string.IsNullOrWhiteSpace(request.Size))
            payload["size"] = request.Size.Replace(":", "x", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(metadata?.NegativePrompt))
            payload["negative_prompt"] = metadata.NegativePrompt;

        if (!string.IsNullOrWhiteSpace(metadata?.Quality))
            payload["quality"] = metadata.Quality;

        if (!string.IsNullOrWhiteSpace(metadata?.Style))
            payload["style"] = metadata.Style;

        if (metadata?.Sync is not null)
            payload["sync"] = metadata.Sync.Value;

        var reqJson = JsonSerializer.Serialize(payload, ImageJson);
        var relativeUrl = isPhotoMaker
            ? $"1/ai/{productId}/images/generations/photo_maker"
            : $"1/ai/{productId}/openai/images/generations";

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Infomaniak image generation failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var images = ExtractB64ImagesAsDataUrls(doc.RootElement);

        if (images.Count == 0)
            throw new InvalidOperationException("Infomaniak image generation returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static bool IsPhotoMakerModel(string model)
        => model.Contains("photomaker", StringComparison.OrdinalIgnoreCase);

    private static List<string> ExtractB64ImagesAsDataUrls(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var images = new List<string>();

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

