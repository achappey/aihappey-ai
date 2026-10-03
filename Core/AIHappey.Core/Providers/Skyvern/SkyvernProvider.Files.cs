using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Skyvern;

public partial class SkyvernProvider
{
    private const int MaxFileBytes = 25 * 1024 * 1024;
    private const int MaxTotalFileBytes = 100 * 1024 * 1024;
    private sealed record Attachment(string Name, string MediaType, byte[] Bytes);

    private static List<string> FileIds(JsonObject options)
    {
        if (options["file_ids"] is null) return [];
        if (options["file_ids"] is not JsonArray array) throw new ArgumentException("Skyvern file_ids must be an array.");
        return array.Select(value => value?.GetValue<string>() is { Length: > 0 } id ? id
            : throw new ArgumentException("Skyvern file_ids must contain non-empty strings.")).Distinct().ToList();
    }

    private static List<Attachment> Attachments(AIRequest request)
    {
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var files = latest?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        if (files.Count > 50) throw new ArgumentException("Skyvern accepts at most 50 files per run.");
        var result = new List<Attachment>();
        var total = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileName((file.Filename ?? "attachment.bin").Replace('\\', '/'));
            var mediaType = file.MediaType ?? "application/octet-stream";
            var encoded = file.Data?.ToString() ?? "";
            if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = encoded.IndexOf(',');
                if (comma < 0 || !encoded[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Skyvern attachments require base64 data URLs.");
                mediaType = encoded[5..comma].Split(';')[0];
                encoded = encoded[(comma + 1)..];
            }
            if (encoded.Length > (MaxFileBytes + 2L) / 3 * 4 + 4096)
                throw new ArgumentException("Skyvern attachments exceed the gateway 25 MB per-file limit.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException error) { throw new ArgumentException("Skyvern attachments must be binary base64, not HTTP URLs or text.", error); }
            total += bytes.Length;
            if (bytes.Length == 0 || bytes.Length > MaxFileBytes || total > MaxTotalFileBytes)
                throw new ArgumentException("Skyvern attachments must be non-empty and within gateway limits (25 MB per file, 100 MB total).");
            if (!MediaTypeHeaderValue.TryParse(mediaType, out _) || name.Contains('\r') || name.Contains('\n'))
                throw new ArgumentException("Invalid Skyvern attachment filename or media type.");
            result.Add(new(name, mediaType, bytes));
        }
        return result;
    }

    private async Task<Reply> Upload(Turn turn, Attachment file, CancellationToken ct)
    {
        using var message = CreateRequest(turn.Transport, HttpMethod.Post, "v1/upload_file");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file.Bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
        form.Add(content, "file", file.Name);
        if (turn.Controls["retention_days"] is { } retention)
            form.Add(new StringContent(retention.GetValue<int>().ToString(System.Globalization.CultureInfo.InvariantCulture)), "retention_days");
        message.Content = form;
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadReply(response, ct);
    }

    private Task<Reply> ListArtifacts(Turn turn, CancellationToken ct)
    {
        var path = $"v1/runs/{Enc(turn.RunId!)}/artifacts";
        if (turn.Controls["artifact_types"] is { } types)
        {
            if (types is not JsonArray values) throw new ArgumentException("Skyvern gateway.artifact_types must be an array.");
            path += "?" + string.Join("&", values.Select(value => "artifact_type=" + Enc(value!.GetValue<string>())));
        }
        return SendJson(turn.Transport, HttpMethod.Get, path, null, ct);
    }

    private async Task CompleteFiles(Turn turn, CancellationToken ct)
    {
        if (turn.Operation == "upload") return;
        if (turn.Operation is "artifacts" or "artifact_content")
        {
            if (turn.Artifacts is not { ValueKind: JsonValueKind.Array }) throw new InvalidOperationException("Skyvern artifact catalog must be an array.");
        }
        else if (turn.RunId is not null && turn.Controls["include_artifacts"]?.GetValue<bool>() == true)
        {
            var artifacts = await ListArtifacts(turn, ct);
            turn.Artifacts = artifacts.Raw;
        }
        var candidates = new List<(string Url, string? Name, string MediaType, JsonElement Raw)>();
        foreach (var file in Array(turn.Reply.Raw, "downloaded_files"))
            if (String(file, "url") is { } url)
                candidates.Add((url, String(file, "filename"), "application/octet-stream", file));
        foreach (var screenshot in Array(turn.Reply.Raw, "screenshot_urls"))
            if (screenshot.ValueKind == JsonValueKind.String && screenshot.GetString() is { } url)
                candidates.Add((url, "screenshot.png", "image/png", screenshot));
        if (turn.Artifacts is { ValueKind: JsonValueKind.Array } list)
        {
            var selectedId = turn.Controls["artifact_id"]?.GetValue<string>();
            var matched = false;
            foreach (var artifact in list.EnumerateArray())
            {
                var id = String(artifact, "artifact_id");
                if (turn.Operation == "artifact_content" && id != selectedId) continue;
                if (turn.Operation == "artifact_content") matched = true;
                if (Flag(artifact, "archived"))
                {
                    if (turn.Operation == "artifact_content") throw new InvalidOperationException("Skyvern artifact is archived and has no accessible content.");
                    continue;
                }
                var type = String(artifact, "artifact_type");
                if (turn.Operation != "artifact_content" && type is not ("download" or "pdf" or "screenshot" or "screenshot_final")) continue;
                // Skyvern's signed URL includes access information not present in an ID.
                if (String(artifact, "signed_url") is not { Length: > 0 } url)
                {
                    if (turn.Operation == "artifact_content") throw new InvalidOperationException("Skyvern did not return a signed content URL for this artifact.");
                    continue;
                }
                candidates.Add((url, id, type?.StartsWith("screenshot", StringComparison.Ordinal) == true ? "image/png"
                    : type == "pdf" ? "application/pdf" : "application/octet-stream", artifact));
            }
            if (turn.Operation == "artifact_content" && !matched) throw new InvalidOperationException("Skyvern artifact was not found in the specified run.");
        }
        var total = 0;
        foreach (var candidate in candidates.DistinctBy(file => file.Url))
        {
            try
            {
                var file = await Download(candidate.Url, candidate.Name, candidate.MediaType, candidate.Raw,
                    Math.Min(MaxFileBytes, MaxTotalFileBytes - total), ct);
                total += Convert.FromBase64String((string)file.Data!).Length;
                turn.Files.Add(file);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (turn.Operation != "artifact_content" && error is HttpRequestException or ArgumentException or InvalidOperationException)
            {
                // A successful browser run stays successful if an optional/expired artifact
                // cannot be downloaded. Keep the original URL in raw metadata and a warning.
                turn.Warnings.Add($"Artifact unavailable: {error.GetType().Name}.");
            }
        }
    }

    private async Task<AIFileContentPart> Download(string url, string? name, string fallbackType, JsonElement raw, int limit, CancellationToken ct)
    {
        if (limit <= 0) throw new InvalidOperationException("Skyvern output files exceed the 100 MB total gateway limit.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.IsLoopback || uri.Port != 443 || !PublicHost(uri.Host))
            throw new ArgumentException("Skyvern artifact URL must use a public HTTPS endpoint.");
        // Unauthenticated client with redirects disabled. Provider API keys and caller
        // headers must never accompany a presigned cloud-storage URL.
        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _transferClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Skyvern output file exceeds gateway size limits.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var target = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length + count > limit) throw new InvalidOperationException("Skyvern output file exceeds gateway size limits.");
            await target.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return new AIFileContentPart
        {
            Type = "file", Filename = name ?? Path.GetFileName(uri.AbsolutePath),
            MediaType = response.Content.Headers.ContentType?.MediaType ?? fallbackType,
            Data = Convert.ToBase64String(target.ToArray()), Metadata = new() { ["skyvern"] = new { raw, url } }
        };
    }

    private static bool PublicHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(host, out var address)) return true;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
    }
}
