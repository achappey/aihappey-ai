using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{
    private static readonly JsonSerializerOptions RewindAIJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static Dictionary<string, object?> CreateRewindAIPayload(
        Dictionary<string, JsonElement>? providerOptions,
        params (string Name, object? Value)[] values)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal);

        foreach (var (name, value) in values)
        {
            if (value is not null)
                payload[name] = value;
        }

        if (providerOptions is not null
            && providerOptions.TryGetValue(nameof(RewindAI).ToLowerInvariant(), out var rawOptions)
            && rawOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static JsonElement? GetRewindAIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
        => providerOptions is not null
           && providerOptions.TryGetValue(nameof(RewindAI).ToLowerInvariant(), out var options)
           && options.ValueKind == JsonValueKind.Object
            ? options.Clone()
            : null;

    private static string ReadRewindAIString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string GetRewindAIAudioBase64(object audio)
    {
        var value = audio is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()
            : audio.ToString();

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(',');
            if (separator < 0)
                throw new ArgumentException("Audio data URL is invalid.", nameof(audio));
            value = value[(separator + 1)..];
        }

        try
        {
            _ = Convert.FromBase64String(value);
            return value;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be base64 encoded or a base64 data URL.", nameof(audio), exception);
        }
    }

    private static string GetRewindAIAudioExtension(string mediaType)
        => mediaType.Split(';', 2)[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/ogg" => ".ogg",
            "audio/webm" => ".webm",
            "audio/flac" or "audio/x-flac" => ".flac",
            _ => ".bin"
        };

    private static string GetRewindAISpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "application/octet-stream"
        };

    private static string GetRewindAIImageMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => MediaTypeNames.Image.Png
        };

    private static bool IsRewindAIAbsoluteUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out _)
           && (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
}
