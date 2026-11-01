using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.BaguetteBot;

public sealed partial class BaguetteBotProvider
{
    private const int MaximumUploadBytes = 30 * 1024 * 1024;

    private static Upload ReadUpload(AIFileContentPart file)
    {
        if (string.IsNullOrWhiteSpace(file.Filename))
            throw new ArgumentException("BaguetteBot inline files require Filename containing the native computer path.");
        byte[] bytes;
        if (file.Data is byte[] data)
            bytes = data;
        else
        {
            var text = file.Data is JsonElement { ValueKind: JsonValueKind.String } json ? json.GetString() : file.Data as string;
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("BaguetteBot files require inline bytes, base64 or a base64 data URL.");
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                throw new NotSupportedException("BaguetteBot input URLs are not downloaded. Upload inline data and mention its path in the task.");
            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = text.IndexOf(',');
                if (comma < 0 || !text[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("BaguetteBot file data URLs must contain base64.");
                text = text[(comma + 1)..];
            }
            if (text.Length > (MaximumUploadBytes + 2L) / 3 * 4)
                throw new ArgumentException("BaguetteBot uploads must not exceed 30 MiB.");
            try { bytes = Convert.FromBase64String(text); }
            catch (FormatException ex) { throw new ArgumentException("Invalid BaguetteBot file base64.", ex); }
        }
        if (bytes.Length > MaximumUploadBytes)
            throw new ArgumentException("BaguetteBot uploads must not exceed 30 MiB.");
        return new Upload(file.Filename, bytes);
    }

    private async Task UploadFilesAsync(Call call, CancellationToken ct)
    {
        foreach (var file in call.Files)
        {
            var raw = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/files",
                new { path = file.Path, content_base64 = Convert.ToBase64String(file.Bytes) }, ct);
            call.UploadedFiles.Add(JsonSerializer.SerializeToElement(new { path = file.Path, response = raw }, Json));
        }
    }

    private static void ValidateDownloads(JsonElement options)
    {
        if (Property(options, "download_paths") is not { } paths)
            return;
        if (paths.ValueKind != JsonValueKind.Array || paths.EnumerateArray()
            .Any(path => path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString())))
            throw new ArgumentException("BaguetteBot download_paths must be an array of non-empty native file paths.");
    }

    private async Task DownloadFilesAsync(Call call, List<AIContentPart> content, CancellationToken ct)
    {
        if (Property(call.Options, "download_paths") is not { } paths)
            return;
        foreach (var path in paths.EnumerateArray())
            content.Add(await DownloadAsync(AgentPath(call) + "/files?path=" + Uri.EscapeDataString(path.GetString()!),
                path.GetString()!, ct));
    }

    private async Task<AIFileContentPart> DownloadAsync(string path, string filename, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new AIFileContentPart
        {
            Type = "file",
            Filename = filename, MediaType = mediaType, Data = $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}"
        };
    }

    private async Task AddExtrasAsync(Call call, List<AIContentPart> content, Dictionary<string, object?> metadata, CancellationToken ct)
    {
        await DownloadFilesAsync(call, content, ct);
        if (Property(call.Options, "live_view") is { } options)
            metadata["baguettebot.live_view"] = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/computer/live", options, ct);
    }
}
