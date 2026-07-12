using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Routmy;

public partial class RoutmyProvider
{
    private async Task<JsonElement> SendRoutmyMediaJsonAsync(
        string endpoint,
        Dictionary<string, object?> payload,
        string mediaKind,
        CancellationToken cancellationToken,
        TimeSpan? requestTimeout = null)
    {
        var json = JsonSerializer.Serialize(payload, RoutmyMediaJsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var timeoutCts = requestTimeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeoutCts is not null)
            timeoutCts.CancelAfter(requestTimeout.Value);

        var effectiveToken = timeoutCts?.Token ?? cancellationToken;
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken);
        var raw = await response.Content.ReadAsStringAsync(effectiveToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Routmy {mediaKind} request failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static void MergeRoutmyProviderOptions(
        Dictionary<string, object?> payload,
        Dictionary<string, JsonElement>? providerOptions,
        ISet<string>? protectedKeys)
    {
        var options = TryGetRoutmyProviderOptions(providerOptions);
        if (options is null)
            return;

        foreach (var property in options.Value.EnumerateObject())
        {
            if (protectedKeys?.Contains(property.Name) == true)
                continue;

            payload[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), RoutmyMediaJsonOptions);
        }
    }

    private static JsonElement? TryGetRoutmyProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return null;

        return providerOptions.TryGetValue("routmy", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : null;
    }

    private static Dictionary<string, JsonElement> BuildRoutmyMediaProviderMetadata(
        Dictionary<string, object?> requestPayload,
        JsonElement responseRoot)
        => "routmy".CreatePrimitiveProviderMetadata(new
        {
            request = requestPayload,
            response = responseRoot.Clone()
        });

    private async Task<(string Base64, string MediaType)?> TryFetchRoutmyAsBase64Async(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return null;

        using var response = await _client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessRoutmyMediaTypeFromUrl(url, MediaTypeNames.Application.Octet);

        return (Convert.ToBase64String(bytes), mediaType);
    }

    private static DateTime? ResolveRoutmyCreatedTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("created", out var created))
            return null;

        return created.ValueKind switch
        {
            JsonValueKind.Number when created.TryGetInt64(out var unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime,
            JsonValueKind.String when DateTime.TryParse(created.GetString(), out var parsed) => parsed.ToUniversalTime(),
            _ => null
        };
    }

    private static string ResolveRoutmyResponseModel(JsonElement root, string fallback)
        => TryGetRoutmyString(root, "model") ?? fallback;

    private static string? TryGetRoutmyString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return property.GetString();
    }

    private static string? TryGetRoutmyNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.String)
            return property.GetString();

        return property.ValueKind == JsonValueKind.Object
            ? TryGetRoutmyString(property, nestedPropertyName)
            : null;
    }

    private static int? TryGetRoutmyInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var intValue))
            return intValue;

        return property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string GuessRoutmyMediaTypeFromUrl(string? url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback;

        var lower = url.ToLowerInvariant();
        if (lower.Contains(".png")) return MediaTypeNames.Image.Png;
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return MediaTypeNames.Image.Jpeg;
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".gif")) return MediaTypeNames.Image.Gif;
        if (lower.Contains(".mp4")) return "video/mp4";
        if (lower.Contains(".webm")) return "video/webm";
        if (lower.Contains(".mov")) return "video/quicktime";
        if (lower.Contains(".mp3")) return "audio/mpeg";
        if (lower.Contains(".wav")) return "audio/wav";
        if (lower.Contains(".flac")) return "audio/flac";

        return fallback;
    }
}
