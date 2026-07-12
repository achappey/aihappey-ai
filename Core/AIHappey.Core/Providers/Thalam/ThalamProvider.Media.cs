using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Thalam;

public partial class ThalamProvider
{
    private static readonly JsonSerializerOptions ThalamJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonElement GetThalamProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null
           && providerOptions.TryGetValue(GetIdentifier(), out var element)
           && element.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? element
            : default;

    private static void MergeThalamProviderOptions(Dictionary<string, object?> payload, JsonElement providerOptions)
    {
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private Dictionary<string, JsonElement> CreateThalamProviderMetadata(object metadata)
        => new()
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, ThalamJsonOptions)
        };

    private async Task<(byte[] Bytes, string MediaType)> DownloadThalamMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var mediaResp = await _client.GetAsync(url, cancellationToken);
        var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!mediaResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Thalam media download failed ({(int)mediaResp.StatusCode}) for '{url}'.");

        var mediaType = mediaResp.Content.Headers.ContentType?.MediaType
            ?? GuessThalamMediaType(url)
            ?? fallbackMediaType;

        return (bytes, mediaType);
    }

    private static string? GuessThalamMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var path = url.Split('?', '#')[0];

        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Png;
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Jpeg;
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return "image/webp";
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            return MediaTypeNames.Image.Gif;
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return "audio/mpeg";
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return "audio/wav";

        return null;
    }

    private static string NormalizeThalamImageInput(string data, string? mediaType)
        => data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? data
            : data.ToDataUrl(string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Png : mediaType);

    private static string NormalizeThalamAudioFormat(string? format, string? mediaType)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return format.Trim().ToLowerInvariant();

        return mediaType?.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            "audio/ogg" or "audio/opus" => "opus",
            "audio/pcm" => "pcm",
            _ => "mp3"
        };
    }

    private static string NormalizeThalamVideoMediaType(string? mediaType, string? url)
        => !string.IsNullOrWhiteSpace(mediaType)
            ? mediaType
            : GuessThalamMediaType(url) ?? "video/mp4";
}
