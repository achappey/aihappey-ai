using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;

namespace AIHappey.Core.MCP.Media;

public static partial class MediaContentHelpers
{
    private const int MaxImageBytes = 10 * 1024 * 1024;

    public static bool TryParseDataUrl(string input, out string mimeType, out string base64)
    {
        mimeType = string.Empty;
        base64 = string.Empty;

        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var comma = input.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
            return false;

        var header = input[5..comma];
        var data = input[(comma + 1)..];
        if (string.IsNullOrWhiteSpace(data))
            return false;

        var parts = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        mimeType = parts[0];
        var isBase64 = parts.Skip(1).Any(p => string.Equals(p, "base64", StringComparison.OrdinalIgnoreCase));
        if (!isBase64)
            return false;

        base64 = data;
        return true;
    }

    public static async Task<(string MimeType, string Base64)> FetchExternalImageAsBase64Async(
        Uri uri,
        IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        await EnsurePublicHttpUri(uri, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var http = httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        var mime = resp.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mime))
        {
            var provider = new FileExtensionContentTypeProvider();
            provider.TryGetContentType(uri.AbsolutePath, out mime);
        }

        if (string.IsNullOrWhiteSpace(mime) || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URL did not return an image content-type. Url='{uri}', contentType='{mime}'.");

        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        var bytes = await ReadToMaxBytesAsync(stream, MaxImageBytes, cts.Token);
        return (mime, Convert.ToBase64String(bytes));
    }

    private static async Task<byte[]> ReadToMaxBytesAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read <= 0)
                break;

            ms.Write(buffer, 0, read);
            if (ms.Length > maxBytes)
                throw new InvalidOperationException($"Remote image exceeds max size ({maxBytes} bytes). ");
        }

        return ms.ToArray();
    }

    private static async Task EnsurePublicHttpUri(Uri uri, CancellationToken ct)
    {
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to fetch localhost URLs.");

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IsPrivateOrLoopback(ip))
                throw new InvalidOperationException("Refusing to fetch private/loopback IP URLs.");
            return;
        }

        var addrs = await Dns.GetHostAddressesAsync(uri.Host, ct);
        if (addrs.Any(IsPrivateOrLoopback))
            throw new InvalidOperationException("Refusing to fetch URLs that resolve to private/loopback IPs.");
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,
                127 => true,
                192 when b[1] == 168 => true,
                172 when b[1] >= 16 && b[1] <= 31 => true,
                169 when b[1] == 254 => true,
                _ => false
            };
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback))
                return true;

            var b = ip.GetAddressBytes();
            var firstByte = b[0];
            var secondNibble = firstByte & 0xFE;

            // fc00::/7 unique local, fe80::/10 link-local
            if (secondNibble == 0xFC || (firstByte == 0xFE && (b[1] & 0xC0) == 0x80))
                return true;
        }

        return false;
    }
}

