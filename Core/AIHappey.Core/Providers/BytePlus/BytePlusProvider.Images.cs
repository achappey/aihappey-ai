using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider
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

        var model = request.Model;
        var isSeedream = IsSeedreamModel(model);
        var isSeedream30 = IsSeedream30Model(model);
        var isSeededit30 = IsSeededit30Model(model);

        if (!isSeedream && !isSeededit30)
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

        var maxReferenceImages = IsSeedream5ProModel(model) ? 10 : 14;
        if (isSeedream && !isSeedream30 && imageInputs.Count > maxReferenceImages)
        {
            warnings.Add(new { type = "unsupported", feature = "files", details = $"{model} supports up to {maxReferenceImages} reference images; extra images were ignored." });
            imageInputs = [.. imageInputs.Take(maxReferenceImages)];
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = request.Prompt,
            ["size"] = string.IsNullOrWhiteSpace(request.Size) ? null : request.Size,
            ["response_format"] = "b64_json"
        };

        if ((isSeedream && !isSeedream30 || isSeededit30) && imageInputs.Count > 0)
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
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static bool IsSeedreamModel(string model)
        => model.StartsWith("seedream-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSeedreamStreamingModel(string model)
        => (model.StartsWith("seedream-4-0", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("seedream-4-5", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("seedream-5-0", StringComparison.OrdinalIgnoreCase))
           && !IsSeedream5ProModel(model);

    private static bool IsSeedream5ProModel(string model)
        => model.Contains("seedream-5-0-pro", StringComparison.OrdinalIgnoreCase);

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
