using System.Text.Json;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    private static byte[] DecodeAgenticsBase64Payload(string value)
    {
        var payload = value.RemoveDataUrlPrefix();

        try
        {
            return Convert.FromBase64String(payload);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Invalid base64 payload.", nameof(value), ex);
        }
    }

    private static string NormalizeAgenticsAudioMediaType(string? mediaType)
        => string.IsNullOrWhiteSpace(mediaType)
            ? "application/octet-stream"
            : mediaType.Trim().ToLowerInvariant();

    private static string GetAgenticsAudioExtension(string? mediaType)
        => mediaType?.Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/x-m4a" => ".m4a",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            "audio/flac" => ".flac",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/aac" => ".aac",
            "audio/webm" => ".webm",
            _ => ".bin"
        };

    private static string? TryGetAgenticsString(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static int? TryGetInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out value))
                return value;
        }

        return null;
    }

    private static float? TryGetFloat(JsonElement element, params string[] propertyNames)
        => TryGetDouble(element, propertyNames) is { } value ? (float)value : null;

    private static double? TryGetDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? TryGetBoolean(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetPropertyIgnoreCase(element, propertyName, out var property))
                continue;

            if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return property.GetBoolean();

            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var value))
                return value;
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        property = default;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(propertyName, out property))
            return true;

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static void MergeAgenticsAudioProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement metadata,
        List<object> warnings,
        HashSet<string> blockedKeys)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            if (blockedKeys.Contains(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
            warnings.Add(new
            {
                type = "passthrough",
                feature = property.Name,
                details = $"Forwarded provider option '{property.Name}' to Agentics audio request payload."
            });
        }
    }
}
