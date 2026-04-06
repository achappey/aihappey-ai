using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{
    private static readonly JsonSerializerOptions ApiAirforceMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<JsonElement> SendMediaGenerationAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var json = JsonSerializer.Serialize(payload, ApiAirforceMediaJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/images/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"ApiAirforce media generation failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static void MergeRawProviderOptions(
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

            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), ApiAirforceMediaJsonOptions);
        }
    }

    private static void AddUnsupportedWarning(List<object> warnings, string feature, string? details = null)
        => warnings.Add(new
        {
            type = "unsupported",
            feature,
            details
        });

    private static string NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        var normalized = model.Trim();

        if (normalized.StartsWith("apiairforce/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["apiairforce/".Length..];

        return normalized;
    }

    private static string ToDataUrl(ImageFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : file.Data.ToDataUrl(file.MediaType);

    private static string ToDataUrl(VideoFile file)
        => file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? file.Data
            : file.Data.ToDataUrl(file.MediaType);

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }

    private static string? TryGetNestedString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static JsonElement? TryGetProviderOptions(Dictionary<string, JsonElement>? providerOptions, string providerIdentifier)
    {
        if (providerOptions is null)
            return null;

        return providerOptions.TryGetValue(providerIdentifier, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static string? ResolveResponseFormat(JsonElement? providerOptions, string fallback)
    {
        if (providerOptions is JsonElement options
            && options.TryGetProperty("response_format", out var responseFormatEl)
            && responseFormatEl.ValueKind == JsonValueKind.String)
        {
            var responseFormat = responseFormatEl.GetString();
            if (!string.IsNullOrWhiteSpace(responseFormat))
                return responseFormat;
        }

        return fallback;
    }

    private async Task<(string Base64, string MediaType)?> TryFetchAsBase64Async(string url, CancellationToken cancellationToken)
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

    private static string ResolveAudioFormat(string? format, string fallback = "mp3")
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

    private static string ResolveAudioMimeType(string format)
        => format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "opus" => "audio/opus",
            _ => MediaTypeNames.Application.Octet
        };

    private static string GuessMediaTypeFromUrl(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback;

        var lower = url.ToLowerInvariant();

        if (lower.Contains(".png")) return MediaTypeNames.Image.Png;
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (lower.Contains(".mp4")) return "video/mp4";
        if (lower.Contains(".webm")) return "video/webm";
        if (lower.Contains(".mov")) return "video/quicktime";
        if (lower.Contains(".mp3")) return "audio/mpeg";
        if (lower.Contains(".wav")) return "audio/wav";
        if (lower.Contains(".flac")) return "audio/flac";

        return fallback;
    }
}
