using System.Net.Mime;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Recraft;

public partial class RecraftProvider
{
    private static readonly JsonSerializerOptions RecraftJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static (byte[] Bytes, string MediaType) DecodeImageFile(ImageFile file)
    {
        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Application.Octet
            : file.MediaType;

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image file data is required.", nameof(file));

        // Accept both plain base64 and data URLs.
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var headerEnd = file.Data.IndexOf(',');
            if (headerEnd > 5)
            {
                var header = file.Data[5..headerEnd];
                var semicolon = header.IndexOf(';');
                if (semicolon > 0)
                    mediaType = header[..semicolon];
                else if (!string.IsNullOrWhiteSpace(header))
                    mediaType = header;
            }
        }

        var b64 = file.Data.RemoveDataUrlPrefix();
        var bytes = Convert.FromBase64String(b64);
        return (bytes, mediaType);
    }

    private static string GuessMimeFromUrl(string url, string fallback)
    {
        if (string.IsNullOrWhiteSpace(url))
            return fallback;

        var clean = url.Split('?', '#')[0];

        if (clean.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
        if (clean.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (clean.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (clean.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (clean.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return "image/gif";
        if (clean.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return "image/bmp";
        if (clean.EndsWith(".avif", StringComparison.OrdinalIgnoreCase)) return "image/avif";
        if (clean.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";

        return fallback;
    }

    private async Task<string> DownloadAsDataUrlAsync(string url, string defaultMime, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mime = response.Content.Headers.ContentType?.MediaType
            ?? GuessMimeFromUrl(url, defaultMime);

        return Convert.ToBase64String(bytes).ToDataUrl(mime);
    }

    private async Task<List<string>> ParseImagesFromResponseAsync(
        string raw,
        string defaultMime,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        List<string> images = [];

        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                if (item.TryGetProperty("b64_json", out var b64El))
                {
                    var b64 = b64El.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                        images.Add(b64.ToDataUrl(defaultMime));
                }

                if (item.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        images.Add(await DownloadAsDataUrlAsync(url, defaultMime, cancellationToken));
                }
            }
        }

        if (root.TryGetProperty("image", out var imageEl))
        {
            if (imageEl.ValueKind == JsonValueKind.Object)
            {
                if (imageEl.TryGetProperty("b64_json", out var b64El))
                {
                    var b64 = b64El.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                        images.Add(b64.ToDataUrl(defaultMime));
                }

                if (imageEl.TryGetProperty("url", out var urlEl))
                {
                    var url = urlEl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                        images.Add(await DownloadAsDataUrlAsync(url, defaultMime, cancellationToken));
                }
            }
            else if (imageEl.ValueKind == JsonValueKind.String)
            {
                var value = imageEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        images.Add(await DownloadAsDataUrlAsync(value, defaultMime, cancellationToken));
                    else
                        images.Add(value.ToDataUrl(defaultMime));
                }
            }
        }

        return images;
    }
}

