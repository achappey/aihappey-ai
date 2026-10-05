using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Notte;

public partial class NotteProvider
{
    private async IAsyncEnumerable<AIFileContentPart> DownloadFiles(Turn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var offset = 0;
        while (true)
        {
            var page = await Record(turn, HttpMethod.Get, $"sessions/{Enc(turn.SessionId!)}/files?source=session_download&limit=100&offset={offset}", null, ct);
            if (Property(page.Raw, "files") is not { ValueKind: JsonValueKind.Array } files) throw new InvalidOperationException("Notte files response has no files array.");
            foreach (var raw in files.EnumerateArray())
            {
                var id = String(raw, "id") ?? throw new InvalidOperationException("Notte session file has no id.");
                if (!seen.Add(id)) continue;
                if (seen.Count > 1000) throw new InvalidOperationException("Notte generated-file count exceeds gateway limit.");
                using var message = Message(HttpMethod.Get, $"sessions/{Enc(turn.SessionId!)}/files/{Enc(id)}", turn.Request);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(150));
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Gone)
                { turn.Warnings.Add($"Generated file '{id}' expired before download."); continue; }
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Notte file download failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(timeout.Token)}", null, response.StatusCode);
                var bytes = await ReadBounded(response.Content, Math.Min(25 * 1024 * 1024, 100 * 1024 * 1024 - totalBytes), timeout.Token);
                totalBytes += bytes.Length;
                var mime = String(raw, "mime_type") ?? response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                yield return new() { Type = "file", Filename = String(raw, "filename"), MediaType = mime,
                    Data = Convert.ToBase64String(bytes), Metadata = new() { ["notte"] = new { raw, session_id = turn.SessionId, file_id = id } } };
            }
            if (files.GetArrayLength() == 0) break;
            offset += files.GetArrayLength();
            var count = Property(page.Raw, "total")?.GetInt32() ?? throw new InvalidOperationException("Notte files response has no total.");
            if (offset >= count) break;
            if (offset >= 10000) throw new InvalidOperationException("Notte file pagination exceeded gateway limit.");
        }
    }
}
