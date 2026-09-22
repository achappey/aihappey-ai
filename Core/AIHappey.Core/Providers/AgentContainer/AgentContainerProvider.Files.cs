using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.AgentContainer;

public partial class AgentContainerProvider
{
    private sealed record AgentContainerInputFile(string Filename, string MediaType, byte[] Bytes);

    private async Task<List<AgentContainerUploadedFile>> UploadAgentContainerInputFilesAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var parts = latestUser?.Content?.OfType<AIFileContentPart>() ?? [];
        var uploaded = new List<AgentContainerUploadedFile>();

        foreach (var part in parts)
        {
            var file = await DecodeAgentContainerInputFileAsync(part, uploaded.Count, cancellationToken);
            var prepared = await SendAgentContainerJsonAsync(
                HttpMethod.Post,
                "uploads/prepare",
                new { filename = file.Filename, contentType = file.MediaType, sizeBytes = file.Bytes.LongLength },
                "prepare file upload",
                idempotencyKey: null,
                cancellationToken);

            var uploadId = GetAgentContainerString(prepared, "uploadId")
                           ?? throw new InvalidOperationException("AgentContainer prepare upload response did not include uploadId.");
            var uploadUrl = GetAgentContainerString(prepared, "uploadUrl")
                            ?? throw new InvalidOperationException("AgentContainer prepare upload response did not include uploadUrl.");
            var method = GetAgentContainerString(prepared, "method") ?? "PUT";
            var storageId = await SendPreparedAgentContainerUploadAsync(prepared, method, uploadUrl, file, cancellationToken);

            var completeBody = new Dictionary<string, object?> { ["uploadId"] = uploadId };
            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(storageId))
                    throw new InvalidOperationException("AgentContainer POST upload did not return storageId.");
                completeBody["storageId"] = storageId;
            }

            var completed = await SendAgentContainerJsonAsync(
                HttpMethod.Post,
                "uploads/complete",
                completeBody,
                "complete file upload",
                idempotencyKey: null,
                cancellationToken);
            var fileId = GetAgentContainerString(completed, "id")
                         ?? throw new InvalidOperationException("AgentContainer complete upload response did not include file id.");
            uploaded.Add(new AgentContainerUploadedFile(
                fileId,
                GetAgentContainerString(completed, "filename") ?? file.Filename,
                GetAgentContainerString(completed, "contentType") ?? file.MediaType,
                completed.Clone()));
        }

        return uploaded;
    }

    private async Task<string?> SendPreparedAgentContainerUploadAsync(
        JsonElement prepared,
        string method,
        string uploadUrl,
        AgentContainerInputFile file,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), uploadUrl);
        if (TryGetAgentContainerProperty(prepared, "headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            foreach (var header in headers.EnumerateObject())
                request.Headers.TryAddWithoutValidation(header.Name, header.Value.ToString());

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            var multipart = new MultipartFormDataContent();
            var bytes = new ByteArrayContent(file.Bytes);
            bytes.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
            multipart.Add(bytes, "file", file.Filename);
            request.Content = multipart;
        }
        else
        {
            request.Content = new ByteArrayContent(file.Bytes);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType);
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"AgentContainer direct upload failed with status {(int)response.StatusCode}: {body}", null, response.StatusCode);

        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body, AgentContainerJson);
            return GetAgentContainerString(json, "storageId")
                   ?? GetAgentContainerString(json, "storage_id")
                   ?? GetAgentContainerString(json, "id");
        }
        catch (JsonException)
        {
            return body.Trim('"', ' ', '\r', '\n');
        }
    }

    private async Task<AgentContainerInputFile> DecodeAgentContainerInputFileAsync(
        AIFileContentPart part,
        int index,
        CancellationToken cancellationToken)
    {
        var filename = string.IsNullOrWhiteSpace(part.Filename) ? $"input-{index + 1}.bin" : Path.GetFileName(part.Filename);
        var mediaType = string.IsNullOrWhiteSpace(part.MediaType) ? MediaTypeNames.Application.Octet : part.MediaType!;
        var data = part.Data?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException($"AgentContainer input file '{filename}' has no data.");

        if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"AgentContainer input file download failed with status {(int)response.StatusCode}.", null, response.StatusCode);
            mediaType = response.Content.Headers.ContentType?.MediaType ?? mediaType;
            return new AgentContainerInputFile(filename, mediaType, await response.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"AgentContainer input file '{filename}' has an invalid data URL.");
            var header = data[5..comma];
            var semicolon = header.IndexOf(';');
            if (semicolon > 0)
                mediaType = header[..semicolon];
            data = data[(comma + 1)..];
        }

        try
        {
            return new AgentContainerInputFile(filename, mediaType, Convert.FromBase64String(data));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException($"AgentContainer input file '{filename}' must be an HTTPS URL, base64, or base64 data URL.", ex);
        }
    }

    private async Task<IReadOnlyList<AgentContainerDownloadedFile>> DownloadAgentContainerOutputFilesAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
        var files = new List<AgentContainerDownloadedFile>();
        string? cursor = null;
        do
        {
            var path = $"tasks/{Uri.EscapeDataString(taskId)}/files?limit=100";
            if (!string.IsNullOrWhiteSpace(cursor))
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            var root = await SendAgentContainerJsonAsync(HttpMethod.Get, path, null, "list task files", null, cancellationToken);
            if (!TryGetAgentContainerProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var raw in data.EnumerateArray())
            {
                var id = GetAgentContainerString(raw, "id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var bytes = await SendAgentContainerBytesAsync(
                    $"tasks/{Uri.EscapeDataString(taskId)}/files/{Uri.EscapeDataString(id)}",
                    "download task file",
                    cancellationToken);
                files.Add(new AgentContainerDownloadedFile(
                    id,
                    GetAgentContainerString(raw, "filename") ?? id,
                    GetAgentContainerString(raw, "contentType") ?? MediaTypeNames.Application.Octet,
                    bytes,
                    raw.Clone()));
            }

            cursor = TryGetAgentContainerProperty(root, "pageInfo", out var pageInfo)
                     && GetAgentContainerBoolean(pageInfo, "hasMore") == true
                ? GetAgentContainerString(pageInfo, "nextCursor")
                : null;
        }
        while (!string.IsNullOrWhiteSpace(cursor));
        return files;
    }
}
