using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
{
    private static readonly JsonSerializerOptions RequestyImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(imageRequest);

        if (string.IsNullOrWhiteSpace(imageRequest.Model))
            throw new ArgumentException("Model is required.", nameof(imageRequest));

        if (string.IsNullOrWhiteSpace(imageRequest.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(imageRequest));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files" });

        if (imageRequest.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(imageRequest.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (imageRequest.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        string? outputFormat = null;
        string? quality = null;
        string? background = null;

        var providerOptions = imageRequest.ProviderOptions;
        if (providerOptions is not null &&
            providerOptions.TryGetValue(GetIdentifier(), out var requestyOptions) &&
            requestyOptions.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(requestyOptions, out var outputFormatValue, "output_format", "outputFormat"))
                outputFormat = outputFormatValue;

            if (TryGetString(requestyOptions, out var qualityValue, "quality"))
                quality = qualityValue;

            if (TryGetString(requestyOptions, out var backgroundValue, "background"))
                background = backgroundValue;
        }

        var payload = new
        {
            model = imageRequest.Model,
            prompt = imageRequest.Prompt,
            n = imageRequest.N,
            size = imageRequest.Size,
            quality,
            background,
            output_format = outputFormat,
            response_format = "b64_json"
        };

        var json = JsonSerializer.Serialize(payload, RequestyImageJson);

        using var response = await _client.PostAsync(
            "v1/images/generations",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"{response.StatusCode}: {raw}");

        var mediaType = ToImageMediaType(outputFormat);
        var images = ExtractB64ImagesAsDataUrls(raw, mediaType);
        if (images.Count == 0)
            throw new Exception("Requesty returned no images.");

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

    private static string ToImageMediaType(string? outputFormat)
        => outputFormat?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };

    private static List<string> ExtractB64ImagesAsDataUrls(string rawJson, string mediaType)
    {
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("b64_json", out var b64El) || b64El.ValueKind != JsonValueKind.String)
                continue;

            var b64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(b64))
                continue;

            images.Add(b64.ToDataUrl(mediaType));
        }

        return images;
    }

    private static bool TryGetString(JsonElement obj, out string? value, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;

            value = el.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}

