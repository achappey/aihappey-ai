using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runtimo;

public partial class RuntimoProvider
{
    private static readonly JsonSerializerOptions RuntimoMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonElement GetImageProviderMetadata(ImageRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default
            : element;
    }

    private static JsonElement GetVideoProviderMetadata(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? default
            : element;
    }

    private static string NormalizeModelPath(string model, string providerId)
    {
        var trimmed = model?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        var prefix = providerId + "/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed.SplitModelId().Model
            : trimmed;
    }

    private static Dictionary<string, object?> BuildRuntimoPayload(
        string prompt,
        string? mediaInput,
        JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            CopyJsonObjectProperties(metadata, payload, static name => !string.Equals(name, "input", StringComparison.OrdinalIgnoreCase));

            if (TryGetPropertyIgnoreCase(metadata, "input", out var metadataInput) && metadataInput.ValueKind == JsonValueKind.Object)
                CopyJsonObjectProperties(metadataInput, input);
        }

        input["prompt"] = prompt;

        if (!string.IsNullOrWhiteSpace(mediaInput))
            input["image_url"] = mediaInput;

        payload["input"] = input;
        return payload;
    }

    private static void CopyJsonObjectProperties(
        JsonElement source,
        IDictionary<string, object?> target,
        Func<string, bool>? predicate = null)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in source.EnumerateObject())
        {
            if (predicate is not null && !predicate(property.Name))
                continue;

            target[property.Name] = property.Value.Clone();
        }
    }

    private Dictionary<string, JsonElement> BuildRuntimoProviderMetadata(string endpoint, JsonElement root)
        => new()
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint,
                body = root
            }, JsonSerializerOptions.Web)
        };

    private static string? NormalizeImageInput(ImageFile? file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Data))
            return null;

        if (LooksLikeUrl(file.Data) || LooksLikeDataUrl(file.Data))
            return file.Data;

        if (string.IsNullOrWhiteSpace(file.MediaType))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }

    private static string? NormalizeVideoInput(VideoFile? file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Data))
            return null;

        if (LooksLikeUrl(file.Data) || LooksLikeDataUrl(file.Data))
            return file.Data;

        if (string.IsNullOrWhiteSpace(file.MediaType))
            return file.Data;

        return file.Data.ToDataUrl(file.MediaType);
    }

    private async Task<string> DownloadImageAsDataUrlAsync(string url, string? fallbackMediaType, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runtimo image download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? fallbackMediaType
            ?? GuessImageMediaTypeFromUrl(url)
            ?? MediaTypeNames.Image.Png;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private async Task<VideoResponseFile> DownloadVideoAsync(string url, string? fallbackMediaType, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runtimo video download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? fallbackMediaType
            ?? GuessVideoMediaTypeFromUrl(url)
            ?? "video/mp4";

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = mediaType
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var value) || value.ValueKind != JsonValueKind.String)
                continue;

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static bool LooksLikeUrl(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeDataUrl(string value)
        => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetDataUrlMediaType(string value)
    {
        if (!LooksLikeDataUrl(value))
            return null;

        var semicolon = value.IndexOf(';');
        var comma = value.IndexOf(',');
        var end = semicolon >= 0 ? semicolon : comma;
        if (end <= 5)
            return null;

        return value[5..end];
    }

    private static string? GuessImageMediaTypeFromUrl(string url)
        => GetPathExtension(url) switch
        {
            ".png" => MediaTypeNames.Image.Png,
            ".jpg" => MediaTypeNames.Image.Jpeg,
            ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".webp" => "image/webp",
            ".gif" => MediaTypeNames.Image.Gif,
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => null
        };

    private static string? GuessVideoMediaTypeFromUrl(string url)
        => GetPathExtension(url) switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            ".gif" => MediaTypeNames.Image.Gif,
            _ => null
        };

    private static string? GetPathExtension(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return Path.GetExtension(uri.AbsolutePath)?.ToLowerInvariant();
    }
}
