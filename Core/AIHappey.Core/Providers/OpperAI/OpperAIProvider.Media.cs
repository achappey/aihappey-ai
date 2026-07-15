using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private static readonly JsonSerializerOptions OpperAIMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record OpperAIMediaDownload(byte[] Bytes, string MediaType);

    private sealed record OpperAIVideoStatus(string Status, JsonElement Root);

    private Dictionary<string, object?> GetOpperAIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (providerOptions is null
            || !providerOptions.TryGetValue(GetIdentifier(), out var providerMetadata)
            || providerMetadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return payload;
        }

        if (providerMetadata.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.");

        foreach (var property in providerMetadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private Dictionary<string, JsonElement> CreateOpperAIMediaMetadata(object? data)
        => GetIdentifier().CreatePrimitiveProviderMetadata(data);

    private static void AddOpperAIParameters(
        Dictionary<string, object?> payload,
        Dictionary<string, object?> providerOptions)
    {
        if (providerOptions.Count == 0)
            return;

        if (providerOptions.TryGetValue("parameters", out var explicitParameters)
            && explicitParameters is JsonElement { ValueKind: JsonValueKind.Object } parametersElement)
        {
            payload["parameters"] = parametersElement.Clone();
        }

        foreach (var option in providerOptions)
        {
            if (string.Equals(option.Key, "parameters", StringComparison.OrdinalIgnoreCase))
                continue;

            payload[option.Key] = option.Value;
        }
    }

    private static StringContent CreateOpperAIJsonContent(object payload)
        => new(
            JsonSerializer.Serialize(payload, OpperAIMediaJsonOptions),
            Encoding.UTF8,
            MediaTypeNames.Application.Json);

    private static string? TryGetOpperAIString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetOpperAIProperty(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.ToString();
        }

        return null;
    }

    private static double? TryGetOpperAIDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetOpperAIProperty(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
                return value;
        }

        return null;
    }

    private static bool TryGetOpperAIProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static DateTime ResolveOpperAITimestamp(JsonElement root, DateTime fallback)
    {
        if (!TryGetOpperAIProperty(root, "created", out var created))
            return fallback;

        if (created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var unixSeconds))
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

        return fallback;
    }

    private static string NormalizeOpperAIDataUrl(string data, string fallbackMediaType)
    {
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data;

        return data.RemoveDataUrlPrefix().ToDataUrl(fallbackMediaType);
    }

    private static string NormalizeOpperAIInputFile(string data, string mediaType)
    {
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("file_", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        return data.ToDataUrl(mediaType);
    }

    private async Task<OpperAIMediaDownload> DownloadOpperAIMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"OpperAI media download failed ({(int)response.StatusCode})."
                : $"OpperAI media download failed ({(int)response.StatusCode}): {error}");
        }

        return new OpperAIMediaDownload(
            bytes,
            response.Content.Headers.ContentType?.MediaType ?? GuessOpperAIMediaType(url) ?? fallbackMediaType);
    }

    private static string? GuessOpperAIMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var withoutQuery = url.Split('?', '#')[0];
        if (withoutQuery.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Png;
        if (withoutQuery.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (withoutQuery.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (withoutQuery.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Gif;
        if (withoutQuery.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
        if (withoutQuery.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return "audio/wav";
        if (withoutQuery.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return "audio/ogg";
        if (withoutQuery.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return "audio/flac";
        if (withoutQuery.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (withoutQuery.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (withoutQuery.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";

        return null;
    }

    private static string ResolveOpperAISpeechMimeType(string? format, string? responseMediaType)
        => (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => responseMediaType ?? "audio/mpeg"
        };

    private static string? ExtractOpperAIVideoUrl(JsonElement root)
    {
        if (TryGetOpperAIString(root, "url", "download_url", "downloadUrl", "video_url", "videoUrl") is { } direct
            && !string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (TryGetOpperAIProperty(root, "artifact", out var artifact)
            && TryGetOpperAIString(artifact, "url", "download_url", "downloadUrl") is { } artifactUrl
            && !string.IsNullOrWhiteSpace(artifactUrl))
        {
            return artifactUrl;
        }

        if (TryGetOpperAIProperty(root, "file", out var file)
            && TryGetOpperAIString(file, "url", "download_url", "downloadUrl") is { } fileUrl
            && !string.IsNullOrWhiteSpace(fileUrl))
        {
            return fileUrl;
        }

        if (TryGetOpperAIProperty(root, "data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var itemUrl = TryGetOpperAIString(item, "url", "download_url", "downloadUrl", "video_url", "videoUrl");
                if (!string.IsNullOrWhiteSpace(itemUrl))
                    return itemUrl;
            }
        }

        return null;
    }

    private static bool IsOpperAITerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpperAISuccessStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);
}
