using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
{
    private static readonly JsonSerializerOptions ImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequestBytePlus(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = request.Model;
        var isSeedream45 = IsSeedream45Model(model);
        var isSeedream40 = IsSeedream40Model(model);
        var isSeedream30 = IsSeedream30Model(model);
        var isSeededit30 = IsSeededit30Model(model);

        if (!isSeedream45 && !isSeedream40 && !isSeedream30 && !isSeededit30)
            throw new NotSupportedException($"BytePlus image model '{request.Model}' is not supported.");

        if (request.N is > 1)
        {
            warnings.Add(new { type = "unsupported", feature = "n", details = "BytePlus image generation returns a single image per request in this integration." });
        }

        if (request.Mask is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "mask" });
        }

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "BytePlus uses explicit size; aspectRatio was ignored." });
        }

        if (request.Seed.HasValue && !(isSeedream30 || isSeededit30))
        {
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Seed is only supported for seedream-3.0-t2i and seededit-3.0-i2i." });
        }

        var imageInputs = new List<string>();
        if (request.Files?.Any() == true)
        {
            foreach (var file in request.Files)
                imageInputs.Add(ToDataUrl(file));
        }

        if (isSeedream30 && imageInputs.Count > 0)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = "seedream-3.0-t2i is text-to-image; input images were ignored." });
            imageInputs.Clear();
        }

        if (isSeededit30)
        {
            if (imageInputs.Count == 0)
                throw new ArgumentException("seededit-3.0-i2i requires a reference image provided in 'files'.", nameof(request));

            if (imageInputs.Count > 1)
            {
                warnings.Add(new { type = "unsupported", feature = "files", details = "seededit-3.0-i2i supports a single reference image; extra images were ignored." });
                imageInputs = [imageInputs[0]];
            }
        }

        if ((isSeedream45 || isSeedream40) && imageInputs.Count > 14)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = "seedream-4.x supports up to 14 reference images; extra images were ignored." });
            imageInputs = imageInputs.Take(14).ToList();
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["response_format"] = "b64_json"
        };

        if ((isSeedream45 || isSeedream40 || isSeededit30) && imageInputs.Count > 0)
        {
            payload["image"] = imageInputs.Count == 1 ? imageInputs[0] : imageInputs;
        }

        if (request.Seed.HasValue && (isSeedream30 || isSeededit30))
        {
            payload["seed"] = request.Seed.Value;
        }

        var json = JsonSerializer.Serialize(payload, ImageJson);
        using var resp = await _client.PostAsync(
            "v3/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"{resp.StatusCode}: {raw}");

        var images = ExtractB64ImagesAsDataUrls(raw, MediaTypeNames.Image.Jpeg);
        if (images.Count == 0)
            throw new Exception("BytePlus returned no images.");

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

    private static bool IsSeedream45Model(string model)
        => model.StartsWith("seedream-4-5", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeedream40Model(string model)
        => model.StartsWith("seedream-4-0", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeedream30Model(string model)
        => model.StartsWith("seedream-3-0-t2i", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("seedream-3-0-t2i", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeededit30Model(string model)
        => model.StartsWith("seededit-3-0-i2i", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("seededit-3-0-i2i", StringComparison.OrdinalIgnoreCase);

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

    private static string ToDataUrl(ImageFile file)
    {
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }
}
