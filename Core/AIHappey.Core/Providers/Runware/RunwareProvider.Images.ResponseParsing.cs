using System.Net.Mime;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private async Task<List<string>> ExtractImagesAsync(string raw, string outputFormat, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return [];

        var mime = OutputFormatToMime(outputFormat);
        var results = new List<string>();

        foreach (var item in dataEl.EnumerateArray())
        {
            if (item.TryGetProperty("imageDataURI", out var dataUriEl) && dataUriEl.ValueKind == JsonValueKind.String)
            {
                var dataUri = dataUriEl.GetString();
                if (!string.IsNullOrWhiteSpace(dataUri))
                    results.Add(dataUri);
                continue;
            }

            if (item.TryGetProperty("imageBase64Data", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    results.Add(b64.ToDataUrl(mime));
                continue;
            }

            if (item.TryGetProperty("imageURL", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var bytes = await _client.GetByteArrayAsync(url, ct);
                results.Add(Convert.ToBase64String(bytes).ToDataUrl(mime));
            }
        }

        return results;
    }

    private static string OutputFormatToMime(string? outputFormat)
        => outputFormat?.Trim().ToUpperInvariant() switch
        {
            "JPG" or "JPEG" => MediaTypeNames.Image.Jpeg,
            "WEBP" => "image/webp",
            _ => MediaTypeNames.Image.Png,
        };
}

