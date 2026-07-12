using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    private static readonly JsonSerializerOptions OneInferJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonElement GetOneInferProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return default;

        return providerOptions.TryGetValue(GetIdentifier(), out var metadata)
            ? metadata
            : default;
    }

    private static Dictionary<string, object?> OneInferJsonObjectToDictionary(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = OneInferJsonElementToObject(property.Value);

        return payload;
    }

    private static object? OneInferJsonElementToObject(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.Clone()
        };

    private static JsonElement OneInferGetData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("data", out var data)
           && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    private static bool TryParseOneInferJson(string raw, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static void AddOneInferMultipartMetadata(
        MultipartFormDataContent form,
        JsonElement metadata,
        Dictionary<string, object?> requestFields,
        params string[] excludedProperties)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        var excluded = new HashSet<string>(excludedProperties, StringComparer.OrdinalIgnoreCase);

        foreach (var property in metadata.EnumerateObject())
        {
            if (excluded.Contains(property.Name))
                continue;

            var value = OneInferJsonElementToFormValue(property.Value);
            if (value is null)
                continue;

            requestFields[property.Name] = value;
            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static string? OneInferJsonElementToFormValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

    private static string? OneInferTryGetString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetRawText();
        }

        return null;
    }

    private static int? OneInferTryGetInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
                return number;
        }

        return null;
    }

    private static float? OneInferTryGetFloat(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();

            if (value.ValueKind == JsonValueKind.String
                && float.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return number;
        }

        return null;
    }

    private static DateTime? ReadOneInferUnixTimestamp(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var seconds = OneInferTryGetInt(element, name);
            if (seconds.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
        }

        return null;
    }

    private static List<TranscriptionSegment> ParseOneInferTranscriptionSegments(JsonElement data)
    {
        var segments = new List<TranscriptionSegment>();

        if (!data.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var segment in segmentsElement.EnumerateArray())
        {
            var text = OneInferTryGetString(segment, "text", "transcript");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = OneInferTryGetFloat(segment, "start", "start_second", "startSecond") ?? 0f;
            var end = OneInferTryGetFloat(segment, "end", "end_second", "endSecond") ?? start;

            if (end < start)
                end = start;

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            });
        }

        return segments;
    }

    private static string OneInferImageMediaTypeFromFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" or MediaTypeNames.Image.Jpeg => MediaTypeNames.Image.Jpeg,
            "webp" or "image/webp" => "image/webp",
            "gif" or MediaTypeNames.Image.Gif => MediaTypeNames.Image.Gif,
            "bmp" or MediaTypeNames.Image.Bmp => MediaTypeNames.Image.Bmp,
            _ => MediaTypeNames.Image.Png
        };

    private static string? OneInferGuessImageMediaType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains(".jpg") || normalized.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (normalized.Contains(".webp")) return "image/webp";
        if (normalized.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (normalized.Contains(".bmp")) return MediaTypeNames.Image.Bmp;
        if (normalized.Contains(".png")) return MediaTypeNames.Image.Png;
        return null;
    }

    private static string NormalizeOneInferAudioFormat(string? format, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(format))
            return format.Trim().ToLowerInvariant();

        return mimeType?.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" => "mp3",
            "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/aac" => "aac",
            "audio/flac" => "flac",
            _ => "mp3"
        };
    }

    private static string ResolveOneInferAudioMimeType(string? format, string? contentType)
        => NormalizeOneInferAudioFormat(format, contentType) switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => contentType ?? "application/octet-stream"
        };

    private static string? OneInferGuessAudioMimeType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains(".mp3")) return "audio/mpeg";
        if (normalized.Contains(".wav")) return "audio/wav";
        if (normalized.Contains(".ogg")) return "audio/ogg";
        if (normalized.Contains(".opus")) return "audio/opus";
        if (normalized.Contains(".aac")) return "audio/aac";
        if (normalized.Contains(".flac")) return "audio/flac";
        return null;
    }

    private static string OneInferVideoMediaTypeFromFormat(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "webm" or "video/webm" => "video/webm",
            "mov" or "quicktime" or "video/quicktime" => "video/quicktime",
            "mkv" or "video/x-matroska" => "video/x-matroska",
            "avi" or "video/x-msvideo" => "video/x-msvideo",
            _ => "video/mp4"
        };

    private static string? OneInferGuessVideoMediaType(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Contains(".webm")) return "video/webm";
        if (normalized.Contains(".mov")) return "video/quicktime";
        if (normalized.Contains(".mkv")) return "video/x-matroska";
        if (normalized.Contains(".avi")) return "video/x-msvideo";
        if (normalized.Contains(".mp4")) return "video/mp4";
        return null;
    }

    private static string? OneInferTryGetDataUrlMediaType(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var separator = value.IndexOf(';');
        if (separator <= "data:".Length)
            return null;

        return value["data:".Length..separator];
    }
}
