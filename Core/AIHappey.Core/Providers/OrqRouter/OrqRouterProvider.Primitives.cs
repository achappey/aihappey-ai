using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider
{
    private static readonly JsonSerializerOptions OrqRouterJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static JsonElement? ReadOrqRouterProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return null;

        return providerOptions.TryGetValue(ProviderId, out var providerElement)
               && providerElement.ValueKind == JsonValueKind.Object
            ? providerElement.Clone()
            : null;
    }

    private static void MergeOrqRouterProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement? providerOptions,
        ISet<string>? reservedKeys = null)
    {
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (reservedKeys?.Contains(property.Name) == true)
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static void AddOrqRouterMultipartProviderOptions(
        MultipartFormDataContent form,
        JsonElement? providerOptions,
        ISet<string>? reservedKeys = null)
    {
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (reservedKeys?.Contains(property.Name) == true)
                continue;

            AddOrqRouterMultipartValue(form, property.Name, property.Value);
        }
    }

    private static void AddOrqRouterMultipartString(MultipartFormDataContent form, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        form.Add(new StringContent(value, Encoding.UTF8), name);
    }

    private static void AddOrqRouterMultipartValue(MultipartFormDataContent form, string name, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.GetRawText()
        };

        AddOrqRouterMultipartString(form, name, text);
    }

    private static string? ReadOrqRouterString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetOrqRouterProperty(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString();

            if (property.ValueKind == JsonValueKind.Number)
                return property.GetRawText();
        }

        return null;
    }

    private static int? ReadOrqRouterInt(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetOrqRouterProperty(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
                return intValue;

            if (property.ValueKind == JsonValueKind.String
                && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static float? ReadOrqRouterFloat(JsonElement element, params string[] propertyNames)
    {
        var value = ReadOrqRouterDouble(element, propertyNames);
        return value is null ? null : (float)value.Value;
    }

    private static double? ReadOrqRouterDouble(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (!TryGetOrqRouterProperty(element, propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var doubleValue))
                return doubleValue;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool TryGetOrqRouterProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static Dictionary<string, JsonElement> BuildOrqRouterProviderMetadata(JsonElement root)
        => ProviderId.CreatePrimitiveProviderMetadata(root.Clone());

    private static DateTime ResolveOrqRouterTimestamp(JsonElement root, DateTime fallback)
    {
        if (root.TryGetProperty("created", out var created)
            && created.ValueKind == JsonValueKind.Number
            && created.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }

        return fallback;
    }

    private static string ResolveOrqRouterAudioMimeType(string? format, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return (format ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "ogg" => "audio/ogg",
            _ => MediaTypeNames.Application.Octet
        };
    }

    private static string ResolveOrqRouterAudioFormat(string? format, string? contentType, string fallback = "mp3")
    {
        if (!string.IsNullOrWhiteSpace(format))
            return format.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(contentType))
            return fallback;

        if (contentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (contentType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
        if (contentType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
        if (contentType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase) || contentType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (contentType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";
        if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return "ogg";

        return fallback;
    }

    private static byte[] DecodeOrqRouterBase64Payload(string value)
    {
        if (MediaContentHelpers.TryParseDataUrl(value, out _, out var parsedBase64))
            value = parsedBase64;
        else
            value = value.RemoveDataUrlPrefix();

        return Convert.FromBase64String(value);
    }

    private static string NormalizeOrqRouterMediaType(string? mediaType, string fallback)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return fallback;

        return mediaType.Split(';', 2)[0].Trim();
    }
}
