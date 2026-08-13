using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    private static readonly HttpClient RemoteAttachmentClient = new();

    private async Task<OpenAiContainerAttachmentPreparation> PrepareContainerAttachmentsAsync(
        ResponseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Tools is null || request.Tools.Count == 0)
            return OpenAiContainerAttachmentPreparation.None;

        var hostedTools = request.Tools.Where(IsHostedContainerTool).ToList();
        if (hostedTools.Count == 0)
            return OpenAiContainerAttachmentPreparation.None;

        var latestUserMessage = request.Input?.Items?
            .OfType<ResponseInputMessage>()
            .LastOrDefault(message => message.Role == ResponseRole.User);
        var attachments = latestUserMessage?.Content.Parts?
            .Where(part => part is InputFilePart or InputImagePart)
            .ToList() ?? [];

        var explicitContainerIds = hostedTools
            .Select(TryGetExplicitToolContainerId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (explicitContainerIds.Count > 1)
        {
            throw new InvalidOperationException(
                "OpenAI Code Interpreter and hosted Shell tools configure multiple distinct container IDs. " +
                "All latest-user attachments must be routed to one container; configure the tools with the same container ID.");
        }

        var hasExplicitContainer = explicitContainerIds.Count == 1;
        var metadataContainerId = ConsumeMetadataContainerId(request.Metadata, GetIdentifier());
        var containerId = hasExplicitContainer
            ? explicitContainerIds[0]
            : metadataContainerId;

        // With neither new attachments nor a reusable/configured container there is
        // no hosted-container lifecycle work to perform.
        if (attachments.Count == 0 && string.IsNullOrWhiteSpace(containerId))
            return OpenAiContainerAttachmentPreparation.None;

        if (!string.IsNullOrWhiteSpace(containerId))
        {
            var status = await GetContainerStatusAsync(containerId, cancellationToken);
            if (!status.IsUsable)
            {
                if (hasExplicitContainer)
                {
                    throw new InvalidOperationException(
                        $"The explicitly configured OpenAI container '{containerId}' is not available ({status.Reason}).");
                }

                containerId = null;
            }
        }

        containerId ??= await CreateContainerAsync(cancellationToken);

        // The Responses API accepts a container only inside the hosted tool shape:
        // Code Interpreter: { type: "code_interpreter", container: "cntr_..." }
        // Shell: { type: "shell", environment: { type: "container_reference", container_id: "cntr_..." } }
        foreach (var tool in hostedTools)
            ConfigureToolContainer(tool, containerId);

        if (attachments.Count == 0)
            return new OpenAiContainerAttachmentPreparation(containerId);

        var uploadedFilenames = new List<string>(attachments.Count);
        for (var index = 0; index < attachments.Count; index++)
        {
            var filename = await UploadAttachmentAsync(
                containerId,
                attachments[index],
                index,
                cancellationToken);
            uploadedFilenames.Add(filename);
        }

        var retainedParts = latestUserMessage.Content.Parts!
            .Where(part => part is not InputFilePart and not InputImagePart)
            .ToList();
        retainedParts.Add(new InputTextPart(
            $"The following attachments are available in the hosted tool container under /mnt/data: " +
            string.Join(", ", uploadedFilenames.Select(static filename => $"/mnt/data/{filename}")) + "."));
        latestUserMessage.Content = new ResponseMessageContent(retainedParts);

        return new OpenAiContainerAttachmentPreparation(containerId);
    }

    private static bool IsHostedContainerTool(ResponseToolDefinition tool)
    {
        if (string.Equals(tool.Type, "code_interpreter", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(tool.Type, "shell", StringComparison.OrdinalIgnoreCase))
            return false;

        if (tool.Extra is null
            || !tool.Extra.TryGetValue("environment", out var environment)
            || environment.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        return !string.Equals(
            TryGetString(environment, "type"),
            "local",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetExplicitToolContainerId(ResponseToolDefinition tool)
    {
        if (tool.Extra is null)
            return null;

        if (string.Equals(tool.Type, "code_interpreter", StringComparison.OrdinalIgnoreCase)
            && tool.Extra.TryGetValue("container", out var container))
        {
            if (container.ValueKind == JsonValueKind.String)
                return container.GetString();

            if (container.ValueKind == JsonValueKind.Object)
                return TryGetString(container, "container_id") ?? TryGetString(container, "id");
        }

        if (string.Equals(tool.Type, "shell", StringComparison.OrdinalIgnoreCase)
            && tool.Extra.TryGetValue("environment", out var environment)
            && environment.ValueKind == JsonValueKind.Object
            && string.Equals(TryGetString(environment, "type"), "container_reference", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetString(environment, "container_id");
        }

        return null;
    }

    private static void ConfigureToolContainer(ResponseToolDefinition tool, string containerId)
    {
        tool.Extra ??= [];

        if (string.Equals(tool.Type, "code_interpreter", StringComparison.OrdinalIgnoreCase))
        {
            tool.Extra["container"] = JsonSerializer.SerializeToElement(containerId);
            return;
        }

        if (!string.Equals(tool.Type, "shell", StringComparison.OrdinalIgnoreCase))
            return;

        Dictionary<string, JsonElement> environment = [];
        if (tool.Extra.TryGetValue("environment", out var existingEnvironment)
            && existingEnvironment.ValueKind == JsonValueKind.Object)
        {
            environment = existingEnvironment.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        }

        environment["type"] = JsonSerializer.SerializeToElement("container_reference");
        environment["container_id"] = JsonSerializer.SerializeToElement(containerId);
        environment.Remove("file_ids");
        tool.Extra["environment"] = JsonSerializer.SerializeToElement(environment, JsonSerializerOptions.Web);
    }

    private static string? ConsumeMetadataContainerId(
        Dictionary<string, object?>? metadata,
        string providerId)
    {
        if (metadata is null || !metadata.TryGetValue(providerId, out var scopedMetadata) || scopedMetadata is null)
            return null;

        try
        {
            var scoped = scopedMetadata is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(scopedMetadata, JsonSerializerOptions.Web);
            if (scoped.ValueKind != JsonValueKind.Object || !scoped.TryGetProperty("container", out var container))
                return null;

            var containerId = container.ValueKind switch
            {
                JsonValueKind.String => container.GetString(),
                JsonValueKind.Object => TryGetString(container, "id") ?? TryGetString(container, "container_id"),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(containerId))
                return null;

            // This is unified lifecycle metadata, not an OpenAI Responses root
            // option. Consume only this property and preserve all other provider
            // options for the existing raw passthrough behavior.
            var retainedProviderOptions = scoped.EnumerateObject()
                .Where(property => !string.Equals(property.Name, "container", StringComparison.Ordinal))
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal);
            metadata[providerId] = JsonSerializer.SerializeToElement(
                retainedProviderOptions,
                JsonSerializerOptions.Web);

            return containerId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<OpenAiContainerStatus> GetContainerStatusAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"v1/containers/{Uri.EscapeDataString(containerId)}");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(false, "not found");

        await EnsureOpenAiSuccessAsync(response, cancellationToken);
        var container = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var status = TryGetString(container, "status");
        if (status is "deleted" or "expired" or "failed")
            return new(false, status);

        if (container.TryGetProperty("expires_after", out var expiresAfter)
            && expiresAfter.ValueKind == JsonValueKind.Object
            && TryGetString(expiresAfter, "anchor") == "last_active_at"
            && expiresAfter.TryGetProperty("minutes", out var minutesElement)
            && minutesElement.TryGetDouble(out var minutes)
            && container.TryGetProperty("last_active_at", out var lastActiveElement)
            && lastActiveElement.TryGetInt64(out var lastActiveAt)
            && DateTimeOffset.FromUnixTimeSeconds(lastActiveAt).AddMinutes(minutes) <= DateTimeOffset.UtcNow)
        {
            return new(false, "expired");
        }

        return new(true, status ?? "available");
    }

    private async Task<string> CreateContainerAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/containers")
        {
            Content = JsonContent.Create(new
            {
                name = $"AIHappey attachments {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC"
            })
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureOpenAiSuccessAsync(response, cancellationToken);
        var container = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
        var containerId = TryGetString(container, "id");
        return !string.IsNullOrWhiteSpace(containerId)
            ? containerId
            : throw new InvalidOperationException("OpenAI created a container without returning its ID.");
    }

    private async Task<string> UploadAttachmentAsync(
        string containerId,
        ResponseContentPart attachment,
        int index,
        CancellationToken cancellationToken)
    {
        var fileId = attachment switch
        {
            InputFilePart file => file.FileId,
            InputImagePart image => image.FileId,
            _ => null
        };
        var requestedFilename = attachment is InputFilePart inputFile ? inputFile.Filename : null;

        if (!string.IsNullOrWhiteSpace(fileId))
        {
            using var copyRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"v1/containers/{Uri.EscapeDataString(containerId)}/files")
            {
                Content = JsonContent.Create(new { file_id = fileId })
            };
            using var copyResponse = await _client.SendAsync(copyRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureOpenAiSuccessAsync(copyResponse, cancellationToken);
            var copiedFile = await copyResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return ResolveUploadedFilename(copiedFile, requestedFilename, fileId, index, null);
        }

        var source = attachment switch
        {
            InputFilePart file => file.FileData ?? file.FileUrl,
            InputImagePart image => image.ImageUrl,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException($"OpenAI attachment {index + 1} has no file data, file URL, or file ID.");

        byte[] bytes;
        string? mediaType;
        string? responseFilename = null;
        if (TryDecodeDataUrl(source, out bytes, out mediaType))
        {
            // Decoded above.
        }
        else if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
                 && sourceUri.Scheme is "http" or "https")
        {
            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, sourceUri);
            var downloadClient = string.Equals(sourceUri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase)
                ? _client
                : RemoteAttachmentClient;
            using var downloadResponse = await downloadClient.SendAsync(
                downloadRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            downloadResponse.EnsureSuccessStatusCode();
            bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            mediaType = downloadResponse.Content.Headers.ContentType?.MediaType;
            responseFilename = downloadResponse.Content.Headers.ContentDisposition?.FileNameStar
                ?? downloadResponse.Content.Headers.ContentDisposition?.FileName
                ?? Path.GetFileName(sourceUri.LocalPath);
        }
        else
        {
            throw new InvalidOperationException(
                $"OpenAI attachment {index + 1} must use a data URL, an HTTP(S) URL, or an OpenAI file ID.");
        }

        mediaType = NormalizeContainerMediaType(mediaType);
        var filename = SanitizeFilename(
            requestedFilename ?? responseFilename,
            index,
            mediaType);

        using var multipart = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        multipart.Add(content, "file", filename);

        using var uploadRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"v1/containers/{Uri.EscapeDataString(containerId)}/files")
        {
            Content = multipart
        };
        using var uploadResponse = await _client.SendAsync(uploadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureOpenAiSuccessAsync(uploadResponse, cancellationToken);
        return filename;
    }

    private static bool TryDecodeDataUrl(string value, out byte[] bytes, out string? mediaType)
    {
        bytes = [];
        mediaType = null;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIndex = value.IndexOf(',');
        if (commaIndex < 5)
            throw new InvalidOperationException("The attachment data URL is malformed.");

        var header = value[5..commaIndex];
        var segments = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        mediaType = segments.FirstOrDefault(segment => segment.Contains('/'));
        var payload = value[(commaIndex + 1)..];
        bytes = segments.Any(segment => string.Equals(segment, "base64", StringComparison.OrdinalIgnoreCase))
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        return true;
    }

    private static string NormalizeContainerMediaType(string? mediaType)
        => mediaType?.ToLowerInvariant() switch
        {
            "application/x-zip-compressed" or "application/x-zip" => "application/zip",
            "image/jpg" => "image/jpeg",
            null or "" => "application/octet-stream",
            var normalized => normalized
        };

    private static string SanitizeFilename(string? filename, int index, string mediaType)
    {
        var fallbackExtension = mediaType switch
        {
            "application/zip" => ".zip",
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "text/plain" => ".txt",
            _ => ".bin"
        };
        var safe = Path.GetFileName(filename?.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(safe))
            safe = $"attachment-{index + 1}{fallbackExtension}";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        return safe;
    }

    private static string ResolveUploadedFilename(
        JsonElement uploadedFile,
        string? requestedFilename,
        string fileId,
        int index,
        string? mediaType)
    {
        var path = TryGetString(uploadedFile, "path");
        return SanitizeFilename(
            requestedFilename ?? (!string.IsNullOrWhiteSpace(path) ? Path.GetFileName(path) : fileId),
            index,
            NormalizeContainerMediaType(mediaType));
    }

    private static async Task EnsureOpenAiSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"OpenAI HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            null,
            response.StatusCode);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record OpenAiContainerAttachmentPreparation(string? ContainerId)
    {
        public static OpenAiContainerAttachmentPreparation None { get; } = new((string?)null);
    }

    private sealed record OpenAiContainerStatus(bool IsUsable, string Reason);
}
