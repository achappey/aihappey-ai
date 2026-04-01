using AIHappey.Common.Extensions;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    private static readonly JsonSerializerOptions BlinkMediaJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static void MergeRawProviderOptions(
        Dictionary<string, object?> payload,
        Dictionary<string, JsonElement>? providerOptions,
        string providerId,
        HashSet<string>? blockedKeys = null)
    {
        if (providerOptions is null || providerOptions.Count == 0)
            return;

        if (providerOptions.TryGetValue(providerId, out var providerRoot)
            && providerRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerRoot.EnumerateObject())
            {
                if (blockedKeys is not null && blockedKeys.Contains(property.Name))
                    continue;

                payload[property.Name] = property.Value.Clone();
            }
        }

        foreach (var option in providerOptions)
        {
            if (string.Equals(option.Key, providerId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (blockedKeys is not null && blockedKeys.Contains(option.Key))
                continue;

            payload[option.Key] = option.Value.Clone();
        }
    }

    private async Task<(string Base64, string MediaType)?> TryFetchAsBase64Async(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        using var response = await _client.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            return null;

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMediaTypeFromUrl(url)
            ?? MediaTypeNames.Application.Octet;

        return (Convert.ToBase64String(bytes), mediaType);
    }

    private static string? GuessMediaTypeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var file = url.Split('?', '#')[0];

        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Png;
        if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Jpeg;
        if (file.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (file.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return MediaTypeNames.Image.Gif;
        if (file.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        if (file.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (file.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";

        return null;
    }

    private static void AddUnsupportedWarning(List<object> warnings, string property, string? details = null)
    {
        warnings.Add(new
        {
            type = "unsupported",
            property,
            details
        });
    }
}

