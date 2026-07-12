using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Vivgrid;

public partial class VivgridProvider
{
    private static readonly JsonSerializerOptions VivgridJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void MergeVivgridProviderOptions(
        Dictionary<string, object?> payload,
        Dictionary<string, JsonElement>? providerOptions,
        string providerIdentifier,
        ISet<string>? blocked = null)
    {
        if (providerOptions is null)
            return;

        if (!providerOptions.TryGetValue(providerIdentifier, out var providerElement)
            || providerElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerElement.EnumerateObject())
        {
            if (blocked?.Contains(property.Name) == true)
                continue;

            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), VivgridJsonOptions);
        }
    }

    private static void MergeVivgridProviderOptions(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? providerOptions,
        string providerIdentifier,
        ISet<string>? blocked = null)
    {
        if (providerOptions is null)
            return;

        if (!providerOptions.TryGetValue(providerIdentifier, out var providerElement)
            || providerElement.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerElement.EnumerateObject())
        {
            if (blocked?.Contains(property.Name) == true)
                continue;

            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            form.Add(new StringContent(JsonElementToFormValue(property.Value), Encoding.UTF8), property.Name);
        }
    }

    private static string JsonElementToFormValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };

    private static string? TryGetPayloadString(IReadOnlyDictionary<string, object?> payload, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!payload.TryGetValue(propertyName, out var value) || value is null)
                continue;

            if (value is string stringValue)
                return string.IsNullOrWhiteSpace(stringValue) ? null : stringValue;

            if (value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.String)
                    return jsonElement.GetString();

                if (jsonElement.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    return jsonElement.GetRawText();
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    private static string ResolveVivgridAudioFormat(string? format, string fallback = "mp3")
    {
        if (string.IsNullOrWhiteSpace(format))
            return fallback;

        var normalized = format.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => normalized
        };
    }

    private static string ResolveVivgridAudioMimeType(string format, string? contentType = null)
        => format switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "pcm" => "audio/L16",
            _ => contentType ?? MediaTypeNames.Application.Octet
        };

    private static string ResolveVivgridImageMimeType(string? format, string? contentType = null)
        => format?.Trim().ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpeg" or "jpg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            _ => contentType ?? MediaTypeNames.Image.Png
        };

    private async Task<(string Base64, string MediaType)?> TryFetchVivgridMediaAsBase64Async(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        using var response = await _client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Octet;
        return (Convert.ToBase64String(bytes), mediaType);
    }

    private static Dictionary<string, JsonElement> BuildVivgridProviderMetadata(JsonElement root)
        => nameof(Vivgrid).ToLowerInvariant().CreatePrimitiveProviderMetadata(root.Clone());
}
