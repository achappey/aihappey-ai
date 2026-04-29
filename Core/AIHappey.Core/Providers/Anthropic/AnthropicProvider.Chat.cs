using Microsoft.AspNetCore.StaticFiles;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    private const string FilesApiBeta = "files-api-2025-04-14";
    private static readonly FileExtensionContentTypeProvider FileContentTypeProvider = new();

    private sealed class ToolCallState
    {
        public string Id { get; }
        public string? Name { get; set; }
        public bool ProviderExecuted { get; set; }
        public StringBuilder InputJson { get; } = new();

        public ToolCallState(string id, string? name = null)
        {
            Id = id;
            Name = name;
        }
    }

    private sealed record AnthropicToolFileDescriptor(
        string FileId,
        string? Filename,
        string? MediaType,
        string? ItemType,
        JsonElement RawItem);

    private sealed record AnthropicDownloadedFile(
        byte[] Bytes,
        string ContentType,
        string? Filename);

    private sealed record AnthropicUploadedFile(
        string Id,
        string? Filename,
        string? MimeType,
        JsonElement Raw);

    private sealed record AnthropicFileMetadata(
        string? Id,
        string? Filename,
        string? MimeType,
        long? SizeBytes,
        bool? Downloadable,
        JsonElement Raw);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
  
    }

    private async Task<List<UIMessagePart>?> TryMapAnthropicToolOutputFileOverrideAsync(
        AIEventEnvelope envelope,
        HashSet<string> emittedFileIds,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.Type, "tool-output-available", StringComparison.OrdinalIgnoreCase))
            return null;

        var data = TryGetJsonElementMap(envelope.Data);
        if (data is null)
            return null;

        if (!TryGetJsonElement(data, "output", out var output))
            return null;

        var toolResultType = TryGetAnthropicProviderMetadataString(data, "type");
        var descriptors = ExtractAnthropicToolFileDescriptors(output, cancellationToken);
        if (descriptors.Count == 0)
            return null;

        if (descriptors.Count == 1
            && string.IsNullOrWhiteSpace(descriptors[0].Filename)
            && TryInferFilenameFromStdout(output) is { } inferredFilename)
        {
            descriptors[0] = descriptors[0] with { Filename = inferredFilename };
        }

        var parts = new List<UIMessagePart>();

        foreach (var descriptor in descriptors)
        {
            if (!emittedFileIds.Add(descriptor.FileId))
                continue;

            var canonicalAnthropicFileUrl = $"https://api.anthropic.com/v1/files/{descriptor.FileId}/content";
            var sourceMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = BuildAnthropicToolFileMetadata(
                    rawData: data,
                    descriptor: descriptor,
                    toolCallId: envelope.Id,
                    toolResultType: toolResultType,
                    canonicalAnthropicFileUrl: canonicalAnthropicFileUrl,
                    filename: descriptor.Filename,
                    mediaType: descriptor.MediaType)
            };

            parts.Add(new SourceUIPart
            {
                Url = canonicalAnthropicFileUrl,
                SourceId = canonicalAnthropicFileUrl,
                Title = descriptor.Filename ?? descriptor.FileId,
                ProviderMetadata = sourceMetadata
            });

            try
            {
                var downloadedFile = await DownloadAnthropicFileAsync(
                    descriptor.FileId,
                    descriptor.Filename,
                    descriptor.MediaType,
                    cancellationToken);

                var resolvedFilename = downloadedFile.Filename
                    ?? descriptor.Filename
                    ?? $"{descriptor.FileId}.bin";

                var resolvedMediaType = ResolveFileContentType(
                    resolvedFilename,
                    downloadedFile.ContentType,
                    descriptor.MediaType);

                var fileMetadata = new Dictionary<string, Dictionary<string, object>?>
                {
                    [GetIdentifier()] = BuildAnthropicToolFileMetadata(
                        rawData: data,
                        descriptor: descriptor,
                        toolCallId: envelope.Id,
                        toolResultType: toolResultType,
                        canonicalAnthropicFileUrl: canonicalAnthropicFileUrl,
                        filename: resolvedFilename,
                        mediaType: resolvedMediaType)
                };

                parts.Add(new FileUIPart
                {
                    MediaType = resolvedMediaType,
                    Url = Convert.ToBase64String(downloadedFile.Bytes),
                    ProviderMetadata = fileMetadata
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep stream resilient: preserve the original tool-output event and emitted source part.
            }
        }

        return parts.Count == 0 ? null : parts;
    }

    private async Task<AnthropicDownloadedFile> DownloadAnthropicFileAsync(
        string fileId,
        string? filenameHint,
        string? mediaTypeHint,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/files/{Uri.EscapeDataString(fileId)}/content");
        request.Headers.TryAddWithoutValidation(betaKey, FilesApiBeta);

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var filename = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        if (!string.IsNullOrWhiteSpace(filename))
            filename = filename.Trim('"');

        filename = string.IsNullOrWhiteSpace(filename)
            ? filenameHint
            : filename;

        var contentType = ResolveFileContentType(
            filename,
            response.Content.Headers.ContentType?.MediaType,
            mediaTypeHint);

        return new AnthropicDownloadedFile(bytes, contentType, filename);
    }

    private async Task<AnthropicFileMetadata?> GetAnthropicFileMetadataAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/files/{Uri.EscapeDataString(fileId)}");
        request.Headers.TryAddWithoutValidation(betaKey, FilesApiBeta);

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement.Clone();

        return new AnthropicFileMetadata(
            TryGetString(root, "id"),
            TryGetString(root, "filename"),
            TryGetString(root, "mime_type"),
            TryGetLong(root, "size_bytes"),
            TryGetBool(root, "downloadable"),
            root);
    }

    private async Task<AnthropicUploadedFile> UploadAnthropicFileAsync(
        byte[] bytes,
        string filename,
        string mediaType,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/files");
        request.Headers.TryAddWithoutValidation(betaKey, FilesApiBeta);

        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(fileContent, "file", filename);
        request.Content = content;

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement.Clone();
        var fileId = TryGetString(root, "id");

        if (string.IsNullOrWhiteSpace(fileId))
            throw new InvalidOperationException("Anthropic Files API upload response did not contain an id.");

        return new AnthropicUploadedFile(
            fileId,
            TryGetString(root, "filename"),
            TryGetString(root, "mime_type"),
            root);
    }

    private static List<AnthropicToolFileDescriptor> ExtractAnthropicToolFileDescriptors(
        JsonElement output,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<AnthropicToolFileDescriptor>();
        var seenFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectAnthropicToolFileDescriptors(output, descriptors, seenFileIds, inheritedItemType: null, inheritedFilename: null, inheritedMediaType: null);

        cancellationToken.ThrowIfCancellationRequested();
        return descriptors;
    }

    private static void CollectAnthropicToolFileDescriptors(
        JsonElement element,
        List<AnthropicToolFileDescriptor> descriptors,
        HashSet<string> seenFileIds,
        string? inheritedItemType,
        string? inheritedFilename,
        string? inheritedMediaType)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var itemType = TryGetString(element, "type") ?? inheritedItemType;
                var filename = TryGetString(element, "filename")
                    ?? TryGetString(element, "file_name")
                    ?? TryGetString(element, "title")
                    ?? inheritedFilename;
                var mediaType = TryGetString(element, "media_type")
                    ?? TryGetString(element, "mime_type")
                    ?? TryGetString(element, "content_type")
                    ?? TryGetString(element, "file_type")
                    ?? inheritedMediaType;

                if (TryGetString(element, "file_id") is { } fileId
                    && !string.IsNullOrWhiteSpace(fileId)
                    && seenFileIds.Add(fileId))
                {
                    descriptors.Add(new AnthropicToolFileDescriptor(
                        fileId,
                        filename,
                        mediaType,
                        itemType,
                        element.Clone()));
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectAnthropicToolFileDescriptors(
                        property.Value,
                        descriptors,
                        seenFileIds,
                        itemType,
                        filename,
                        mediaType);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAnthropicToolFileDescriptors(
                        item,
                        descriptors,
                        seenFileIds,
                        inheritedItemType,
                        inheritedFilename,
                        inheritedMediaType);
                }
                break;
        }
    }

    private static string ResolveFileContentType(
        string? filename,
        string? responseContentType,
        string? metadataContentType)
    {
        if (!string.IsNullOrWhiteSpace(responseContentType))
            return responseContentType;

        if (!string.IsNullOrWhiteSpace(metadataContentType))
            return metadataContentType;

        if (!string.IsNullOrWhiteSpace(filename)
            && FileContentTypeProvider.TryGetContentType(filename, out var detectedContentType))
        {
            return detectedContentType;
        }

        return "application/octet-stream";
    }

    private static string? TryInferFilenameFromStdout(JsonElement output)
    {
        var stdout = TryGetString(output, "stdout");
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var candidates = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
            .Where(IsLikelyFilename)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count == 1
            ? candidates[0]
            : null;
    }

    private static bool IsLikelyFilename(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.EndsWith(':')
            || !value.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private Dictionary<string, object> BuildAnthropicToolFileMetadata(
        Dictionary<string, JsonElement> rawData,
        AnthropicToolFileDescriptor descriptor,
        string? toolCallId,
        string? toolResultType,
        string canonicalAnthropicFileUrl,
        string? filename,
        string? mediaType)
    {
        var metadata = new Dictionary<string, object>
        {
            ["type"] = "anthropic_tool_file",
            ["file_id"] = descriptor.FileId,
            ["anthropic_file_url"] = canonicalAnthropicFileUrl,
            ["anthropic_beta"] = FilesApiBeta,
            ["raw"] = JsonSerializer.SerializeToElement(rawData, JsonSerializerOptions.Web),
            ["raw_item"] = descriptor.RawItem.Clone()
        };

        if (!string.IsNullOrWhiteSpace(filename))
            metadata["filename"] = filename;

        if (!string.IsNullOrWhiteSpace(mediaType))
            metadata["media_type"] = mediaType;

        if (!string.IsNullOrWhiteSpace(toolCallId))
            metadata["tool_use_id"] = toolCallId;

        if (!string.IsNullOrWhiteSpace(toolResultType))
            metadata["tool_result_type"] = toolResultType;

        if (!string.IsNullOrWhiteSpace(descriptor.ItemType))
            metadata["file_item_type"] = descriptor.ItemType;

        return metadata;
    }

    private static Dictionary<string, JsonElement>? TryGetJsonElementMap(object? data)
    {
        if (data is null)
            return null;

        if (data is Dictionary<string, JsonElement> typed)
            return typed;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
                JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private string? TryGetAnthropicProviderMetadataString(
        Dictionary<string, JsonElement> data,
        string key)
    {
        if (!TryGetJsonElement(data, "providerMetadata", out var providerMetadata)
            || providerMetadata.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var provider in providerMetadata.EnumerateObject())
        {
            if (!string.Equals(provider.Name, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
                || provider.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            return TryGetString(provider.Value, key);
        }

        return null;
    }

    private static bool TryGetJsonElement(
        Dictionary<string, JsonElement> data,
        string key,
        out JsonElement value)
    {
        foreach (var item in data)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            return property.Value.GetString();
        }

        return null;
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt64(out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        return null;
    }

}
