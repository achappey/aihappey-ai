using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.DevicAI;

public partial class DevicAIProvider
{
    private async Task<DevicAIAttachments> ResolveAssistantAttachmentsAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var latest = (request.Input?.Items ?? [])
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var parts = latest?.Content?.OfType<AIFileContentPart>().ToList() ?? [];
        var files = new List<object>();
        var images = new List<object>();
        var uploads = new List<JsonElement>();

        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            var name = string.IsNullOrWhiteSpace(part.Filename) ? $"attachment-{index + 1}" : part.Filename!;
            var mediaType = string.IsNullOrWhiteSpace(part.MediaType)
                ? MediaTypeNames.Application.Octet
                : part.MediaType!;
            var raw = GetFileValue(part.Data);
            if (string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException($"DevicAI attachment '{name}' has no data.");

            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    images.Add(new { imageUrl = raw });
                else
                    files.Add(new { name, donwloadUrl = raw, fileType = ToAssistantFileType(mediaType, null) });
                continue;
            }

            var bytes = DecodeFile(raw, ref mediaType, name);
            var uploaded = await UploadFileAsync(name, mediaType, bytes, cancellationToken);
            uploads.Add(uploaded.Raw);
            if (uploaded.IsImage)
                images.Add(new { imageUrl = uploaded.DownloadUrl });
            else
                files.Add(new
                {
                    name = uploaded.Name,
                    donwloadUrl = uploaded.DownloadUrl,
                    fileType = ToAssistantFileType(mediaType, uploaded.FileType)
                });
        }

        return new DevicAIAttachments(files, images, uploads);
    }

    private static void RejectAgentAttachments(AIRequest request)
    {
        if ((request.Input?.Items ?? []).Any(item =>
                (item.Content ?? []).OfType<AIFileContentPart>().Any()))
            throw new NotSupportedException(
                "DevicAI agent thread attachment payloads are not documented. Attachments are currently supported for DevicAI assistants only.");
    }

    private async Task<DevicAIUploadedFile> UploadFileAsync(
        string name,
        string mediaType,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, "files/upload");
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        form.Add(content, "file", Path.GetFileName(name));
        request.Content = form;

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"DevicAI upload file failed with status {(int)response.StatusCode} ({response.StatusCode}): {body}",
                null,
                response.StatusCode);

        var raw = JsonSerializer.Deserialize<JsonElement>(body, DevicAIJson).Clone();
        var downloadUrl = GetString(raw, "downloadUrl")
                          ?? throw new InvalidOperationException("DevicAI file upload response did not include downloadUrl.");
        var fileType = GetString(raw, "fileType") ?? "other";
        var returnedName = GetString(raw, "name") ?? name;
        return new DevicAIUploadedFile(
            returnedName,
            downloadUrl,
            fileType,
            raw,
            string.Equals(fileType, "image", StringComparison.OrdinalIgnoreCase)
            || mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] DecodeFile(string raw, ref string mediaType, string name)
    {
        var base64 = raw;
        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = raw.IndexOf(',');
            if (comma < 0 || !raw[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"DevicAI attachment '{name}' has an invalid data URL.");
            var header = raw[5..comma];
            var semicolon = header.IndexOf(';');
            if (semicolon > 0)
                mediaType = header[..semicolon];
            base64 = raw[(comma + 1)..];
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"DevicAI attachment '{name}' contains invalid base64.", exception);
        }
    }

    private static string? GetFileValue(object? data)
        => data switch
        {
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => data?.ToString()
        };

    private static string ToAssistantFileType(string mediaType, string? providerType)
    {
        if (!string.IsNullOrWhiteSpace(providerType))
            return providerType.ToUpperInvariant() switch
            {
                "IMAGE" => "IMAGE",
                "DOCUMENT" => "DOCUMENT",
                "VIDEO" => "VIDEO",
                "AUDIO" => "AUDIO",
                _ => "OTHER"
            };

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return "IMAGE";
        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return "VIDEO";
        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return "AUDIO";
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("document", StringComparison.OrdinalIgnoreCase)) return "DOCUMENT";
        return "OTHER";
    }
}
