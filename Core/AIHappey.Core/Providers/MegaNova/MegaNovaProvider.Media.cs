using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private static JsonElement GetMegaNovaProviderMetadata(VideoRequest request, string providerId)
        => request.GetProviderMetadata<JsonElement>(providerId);

    private static JsonElement GetMegaNovaProviderMetadata(SpeechRequest request, string providerId)
        => request.GetProviderMetadata<JsonElement>(providerId);

    private static JsonElement GetMegaNovaProviderMetadata(TranscriptionRequest request, string providerId)
        => request.GetProviderMetadata<JsonElement>(providerId);

    private static void MergeMegaNovaProviderMetadata(Dictionary<string, object?> payload, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static void MergeMegaNovaProviderMetadata(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, "file", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddMegaNovaFormValue(form, property.Name, property.Value);
        }
    }

    private static void AddMegaNovaFormValue(MultipartFormDataContent form, string name, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddMegaNovaFormValue(form, name, item);
                break;
            case JsonValueKind.Object:
                form.Add(new StringContent(value.GetRawText()), name);
                break;
            case JsonValueKind.String:
                form.Add(new StringContent(value.GetString() ?? string.Empty), name);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                form.Add(new StringContent(value.GetRawText()), name);
                break;
        }
    }

    private static string ReadMegaNovaAudioBase64(TranscriptionRequest request)
    {
        var audioString = request.Audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            return parsedBase64;

        return audioString;
    }

    private static string GetMegaNovaAudioExtension(string? mediaType)
        => mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
            "audio/flac" => ".flac",
            "audio/aac" => ".aac",
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/webm" => ".webm",
            "audio/mp4" or "audio/m4a" => ".m4a",
            _ => ".bin"
        };

    private static string ResolveMegaNovaAudioFormat(string? requestedFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
            if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        }

        return "mp3";
    }

    private static string ResolveMegaNovaAudioMimeType(string format, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return format.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "application/octet-stream"
        };
    }

    private static string? TryGetMegaNovaString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.GetRawText();
        }

        return null;
    }

    private static float? TryGetMegaNovaFloat(JsonElement element, params string[] names)
    {
        var value = TryGetMegaNovaString(element, names);
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
