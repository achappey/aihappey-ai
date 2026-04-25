using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    private const string DownloadFileToolName = "download_file";
    private const int SyntheticAnthropicFileStreamIndexBase = 100_000;

    private async Task<MessagesResponse> EnrichMessagesResponseWithAnthropicFilesAsync(
        MessagesResponse response,
        CancellationToken cancellationToken)
    {
        var content = response.Content?.ToList() ?? [];
        if (content.Count == 0)
            return response;

        var enrichedContent = new List<MessageContentBlock>(content.Count);
        var seenFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in content)
        {
            enrichedContent.Add(block);

            if (!IsAnthropicFileSourceToolResult(block))
                continue;

            var descriptors = ExtractAnthropicFileDescriptors(block, cancellationToken);
            InferSingleMissingFilename(descriptors, block);

            foreach (var descriptor in descriptors)
            {
                if (!seenFileIds.Add(descriptor.FileId))
                    continue;

                var download = await CreateAnthropicFileDownloadResultAsync(
                    descriptor,
                    block.ToolUseId,
                    block.Type,
                    cancellationToken);

                enrichedContent.Add(CreateAnthropicDownloadToolUseBlock(download));
                enrichedContent.Add(CreateAnthropicDownloadToolResultBlock(download));

            }
        }

        response.Content = enrichedContent;
        return response;
    }

    private void TrackAnthropicFileOutputBlock(
        MessageStreamPart? part,
        AnthropicMessagesFileEnrichmentState state)
    {
        if (!string.Equals(part?.Type, "content_block_start", StringComparison.OrdinalIgnoreCase)
            || part?.Index is null
            || part.ContentBlock is null
            || !IsAnthropicFileSourceToolResult(part.ContentBlock))
        {
            return;
        }

        state.SourceBlocks[part.Index.Value] = part.ContentBlock;
    }

    private async Task<List<MessageStreamPart>> CreateAnthropicFileDownloadStreamPartsAsync(
        MessageStreamPart part,
        AnthropicMessagesFileEnrichmentState state,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(part.Type, "content_block_stop", StringComparison.OrdinalIgnoreCase)
            || part.Index is null
            || !state.SourceBlocks.Remove(part.Index.Value, out var sourceBlock))
        {
            return [];
        }

        var descriptors = ExtractAnthropicFileDescriptors(sourceBlock, cancellationToken);
        InferSingleMissingFilename(descriptors, sourceBlock);
        if (descriptors.Count == 0)
            return [];

        var parts = new List<MessageStreamPart>();

        foreach (var descriptor in descriptors)
        {
            if (!state.SeenFileIds.Add(descriptor.FileId))
                continue;

            var download = await CreateAnthropicFileDownloadResultAsync(
                descriptor,
                sourceBlock.ToolUseId,
                sourceBlock.Type,
                cancellationToken);

            var toolUseIndex = state.NextSyntheticIndex++;
            var toolResultIndex = state.NextSyntheticIndex++;

            parts.Add(new MessageStreamPart
            {
                Type = "content_block_start",
                Index = toolUseIndex,
                ContentBlock = CreateAnthropicDownloadToolUseBlock(download)
            });

            parts.Add(new MessageStreamPart
            {
                Type = "content_block_stop",
                Index = toolUseIndex
            });

            parts.Add(new MessageStreamPart
            {
                Type = "content_block_start",
                Index = toolResultIndex,
                ContentBlock = CreateAnthropicDownloadToolResultBlock(download)
            });

            parts.Add(new MessageStreamPart
            {
                Type = "content_block_stop",
                Index = toolResultIndex
            });

        }

        return parts;
    }

    private async Task<AnthropicFileDownloadResult> CreateAnthropicFileDownloadResultAsync(
        AnthropicToolFileDescriptor descriptor,
        string? sourceToolCallId,
        string? sourceToolResultType,
        CancellationToken cancellationToken)
    {
        var canonicalAnthropicFileUrl = $"https://api.anthropic.com/v1/files/{descriptor.FileId}/content";
        var toolCallId = Guid.NewGuid().ToString("n");

        try
        {
            var fileMetadata = await TryGetAnthropicFileMetadataAsync(descriptor.FileId, cancellationToken);

            var downloadedFile = await DownloadAnthropicFileAsync(
                descriptor.FileId,
                fileMetadata?.Filename ?? descriptor.Filename,
                fileMetadata?.MimeType ?? descriptor.MediaType,
                cancellationToken);

            var resolvedFilename = downloadedFile.Filename
                ?? fileMetadata?.Filename
                ?? descriptor.Filename
                ?? $"{descriptor.FileId}.bin";

            var resolvedMediaType = ResolveFileContentType(
                resolvedFilename,
                downloadedFile.ContentType,
                fileMetadata?.MimeType ?? descriptor.MediaType);

            var providerMetadata = BuildAnthropicDownloadProviderMetadata(
                descriptor,
                fileMetadata,
                sourceToolCallId,
                sourceToolResultType,
                canonicalAnthropicFileUrl,
                resolvedFilename,
                resolvedMediaType,
                isError: false,
                error: null);

            var result = new AnthropicFileDownloadResult
            {
                ToolCallId = toolCallId,
                SourceToolCallId = sourceToolCallId,
                SourceToolResultType = sourceToolResultType,
                FileId = descriptor.FileId,
                Filename = resolvedFilename,
                MediaType = resolvedMediaType,
                CanonicalUrl = canonicalAnthropicFileUrl,
                Bytes = downloadedFile.Bytes,
                DataUrl = ToDataUrl(downloadedFile.Bytes, resolvedMediaType),
                ProviderMetadata = providerMetadata,
                Input = CreateAnthropicDownloadInput(descriptor.FileId, resolvedFilename, resolvedMediaType, canonicalAnthropicFileUrl)
            };

            result.Output = CreateAnthropicDownloadToolCallResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var resolvedFilename = descriptor.Filename ?? $"{descriptor.FileId}.bin";
            var resolvedMediaType = ResolveFileContentType(
                resolvedFilename,
                null,
                descriptor.MediaType);
            var error = CreateDownloadError(exception);
            var providerMetadata = BuildAnthropicDownloadProviderMetadata(
                descriptor,
                fileMetadata: null,
                sourceToolCallId,
                sourceToolResultType,
                canonicalAnthropicFileUrl,
                resolvedFilename,
                resolvedMediaType,
                isError: true,
                error: error);

            var result = new AnthropicFileDownloadResult
            {
                ToolCallId = toolCallId,
                SourceToolCallId = sourceToolCallId,
                SourceToolResultType = sourceToolResultType,
                FileId = descriptor.FileId,
                Filename = resolvedFilename,
                MediaType = resolvedMediaType,
                CanonicalUrl = canonicalAnthropicFileUrl,
                ProviderMetadata = providerMetadata,
                IsError = true,
                Error = error,
                Input = CreateAnthropicDownloadInput(descriptor.FileId, resolvedFilename, resolvedMediaType, canonicalAnthropicFileUrl)
            };

            result.Output = CreateAnthropicDownloadToolCallErrorResult(result);
            return result;
        }
    }

    private async Task<AnthropicFileMetadata?> TryGetAnthropicFileMetadataAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetAnthropicFileMetadataAsync(fileId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static MessageContentBlock CreateAnthropicDownloadToolUseBlock(AnthropicFileDownloadResult download)
        => new()
        {
            Type = "mcp_tool_use",
            Id = download.ToolCallId,
            Name = DownloadFileToolName,
            Input = download.Input,
            AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
            {
                ["provider_executed"] = true,
                ["provider_metadata"] = download.ProviderMetadata
            })
        };

    private static MessageContentBlock CreateAnthropicDownloadToolResultBlock(AnthropicFileDownloadResult download)
        => new()
        {
            Type = "mcp_tool_result",
            ToolUseId = download.ToolCallId,
            Content = new MessagesContent(JsonSerializer.SerializeToElement(download.Output, JsonSerializerOptions.Web)),
            IsError = download.IsError,
            ToolName = DownloadFileToolName,
            AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
            {
                ["name"] = DownloadFileToolName,
                ["provider_executed"] = true,
                ["provider_metadata"] = download.ProviderMetadata,
                ["source_tool_use_id"] = download.SourceToolCallId,
                ["source_tool_result_type"] = download.SourceToolResultType
            })
        };

    private static ResponseResult EnrichResponseResultWithAnthropicDownloadToolCalls(
        ResponseResult response,
        AIResponse unifiedResponse)
    {
        var output = response.Output?.ToList() ?? [];

        foreach (var toolPart in unifiedResponse.Output?.Items?
                     .SelectMany(item => item.Content ?? [])
                     .OfType<AIToolCallContentPart>() ?? [])
        {
            if (toolPart.ProviderExecuted != true
                || !string.Equals(toolPart.ToolName ?? toolPart.Title, DownloadFileToolName, StringComparison.OrdinalIgnoreCase)
                || toolPart.Output is null)
            {
                continue;
            }

            output.Add(new
            {
                type = "custom_tool_call",
                id = toolPart.ToolCallId,
                name = DownloadFileToolName,
                status = string.Equals(toolPart.State, "output-error", StringComparison.OrdinalIgnoreCase)
                    ? "failed"
                    : "completed",
                input = toolPart.Input,
                output = toolPart.Output,
                provider_executed = true,
                provider_metadata = TryGetAnthropicProviderMetadata(toolPart.Metadata)
            });
        }

        response.Output = output;
        return response;
    }

    private static bool TryGetCallToolResult(object output, out CallToolResult callToolResult)
    {
        callToolResult = default!;

        var candidate = output switch
        {
            CallToolResult typed => typed,
            JsonElement json => JsonSerializer.Deserialize<CallToolResult>(json.GetRawText(), JsonSerializerOptions.Web),
            _ => JsonSerializer.Deserialize<CallToolResult>(JsonSerializer.Serialize(output, JsonSerializerOptions.Web), JsonSerializerOptions.Web)
        };

        if (candidate is null)
            return false;

        callToolResult = candidate;
        return true;
    }

    private CallToolResult CreateAnthropicDownloadToolCallResult(AnthropicFileDownloadResult download)
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = download.FileId,
                filename = download.Filename,
                media_type = download.MediaType,
                url = download.CanonicalUrl,
                data_url = download.DataUrl
            }, JsonSerializerOptions.Web)
        };

        if (download.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            result.Content =
            [
                ImageContentBlock.FromBytes(download.Bytes, download.MediaType)
            ];

            return result;
        }

        if (IsTextLikeMediaType(download.MediaType))
        {
            result.Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = download.CanonicalUrl,
                        MimeType = download.MediaType,
                        Text = DecodeText(download.Bytes)
                    }
                }
            ];

            return result;
        }

        result.Content =
        [
            new EmbeddedResourceBlock
            {
                Resource = new BlobResourceContents
                {
                    Uri = download.CanonicalUrl,
                    MimeType = download.MediaType,
                    Blob = download.Bytes
                }
            }
        ];

        return result;
    }

    private static CallToolResult CreateAnthropicDownloadToolCallErrorResult(AnthropicFileDownloadResult download)
        => new()
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = download.FileId,
                filename = download.Filename,
                media_type = download.MediaType,
                url = download.CanonicalUrl,
                error = download.Error
            }, JsonSerializerOptions.Web),
            Content =
            [
                new TextContentBlock
                {
                    Text = $"Failed to download Anthropic output file '{download.FileId}': {download.Error?.Message ?? "Unknown error"}"
                }
            ]
        };

    private Dictionary<string, Dictionary<string, object>> BuildAnthropicDownloadProviderMetadata(
        AnthropicToolFileDescriptor descriptor,
        AnthropicFileMetadata? fileMetadata,
        string? sourceToolCallId,
        string? sourceToolResultType,
        string canonicalAnthropicFileUrl,
        string? filename,
        string? mediaType,
        bool isError,
        AnthropicFileDownloadError? error)
    {
        var metadata = BuildAnthropicToolFileMetadata(
            rawData: [],
            descriptor: descriptor,
            toolCallId: sourceToolCallId,
            toolResultType: sourceToolResultType,
            canonicalAnthropicFileUrl: canonicalAnthropicFileUrl,
            filename: filename,
            mediaType: mediaType);

        metadata["tool_name"] = DownloadFileToolName;
        metadata["name"] = DownloadFileToolName;
        metadata["download_tool"] = true;
        metadata["download_url"] = canonicalAnthropicFileUrl;
        metadata["is_error"] = isError;

        if (!string.IsNullOrWhiteSpace(filename))
            metadata["filename"] = filename;

        if (!string.IsNullOrWhiteSpace(fileMetadata?.Filename))
            metadata["metadata_filename"] = fileMetadata.Filename!;

        if (!string.IsNullOrWhiteSpace(fileMetadata?.MimeType))
            metadata["metadata_mime_type"] = fileMetadata.MimeType!;

        if (fileMetadata?.SizeBytes is not null)
            metadata["metadata_size_bytes"] = fileMetadata.SizeBytes.Value;

        if (fileMetadata?.Downloadable is not null)
            metadata["metadata_downloadable"] = fileMetadata.Downloadable.Value;

        if (fileMetadata is not null)
            metadata["file_metadata"] = fileMetadata.Raw.Clone();

        if (error is not null)
            metadata["error"] = JsonSerializer.SerializeToElement(error, JsonSerializerOptions.Web);

        return new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = metadata
        };
    }

    private static JsonElement CreateAnthropicDownloadInput(
        string fileId,
        string filename,
        string mediaType,
        string canonicalAnthropicFileUrl)
        => JsonSerializer.SerializeToElement(new
        {
            file_id = fileId,
            file_name = filename,
            file_type = mediaType,
            anthropic_file_url = canonicalAnthropicFileUrl
        }, JsonSerializerOptions.Web);

    private static string? TryGetStructuredString(JsonElement structuredContent, string propertyName)
    {
        if (structuredContent.ValueKind != JsonValueKind.Object
            || !structuredContent.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static AnthropicFileDownloadError CreateDownloadError(Exception exception)
        => new(
            exception.GetType().Name,
            exception is HttpRequestException httpRequestException
                ? httpRequestException.StatusCode
                : null,
            exception.Message);

    private static List<AnthropicToolFileDescriptor> ExtractAnthropicFileDescriptors(
        MessageContentBlock block,
        CancellationToken cancellationToken)
    {
        if (block.Content is { IsRaw: true })
            return ExtractAnthropicToolFileDescriptors(block.Content.Raw!.Value, cancellationToken);

        if (block.Content is { IsBlocks: true })
            return ExtractAnthropicToolFileDescriptors(JsonSerializer.SerializeToElement(block.Content.Blocks, JsonSerializerOptions.Web), cancellationToken);

        if (block.Content is { IsText: true } && !string.IsNullOrWhiteSpace(block.Content.Text))
        {
            try
            {
                using var document = JsonDocument.Parse(block.Content.Text);
                return ExtractAnthropicToolFileDescriptors(document.RootElement.Clone(), cancellationToken);
            }
            catch
            {
                return [];
            }
        }

        return [];
    }

    private static void InferSingleMissingFilename(
        List<AnthropicToolFileDescriptor> descriptors,
        MessageContentBlock sourceBlock)
    {
        if (descriptors.Count != 1 || !string.IsNullOrWhiteSpace(descriptors[0].Filename))
            return;

        if (sourceBlock.Content?.IsRaw == true
            && sourceBlock.Content.Raw is JsonElement raw
            && TryInferFilenameFromStdout(raw) is { } rawFilename)
        {
            descriptors[0] = descriptors[0] with { Filename = rawFilename };
            return;
        }

        if (sourceBlock.Content?.IsText == true)
        {
            try
            {
                using var document = JsonDocument.Parse(sourceBlock.Content.Text ?? string.Empty);
                if (TryInferFilenameFromStdout(document.RootElement) is { } textFilename)
                    descriptors[0] = descriptors[0] with { Filename = textFilename };
            }
            catch
            {
                // Not JSON or no stdout shape available.
            }
        }
    }

    private static bool IsAnthropicFileSourceToolResult(MessageContentBlock block)
        => block.Type is "bash_code_execution_tool_result"
            or "code_execution_tool_result"
            or "text_editor_code_execution_tool_result";

    private static bool IsTextLikeMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return false;

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("csv", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeText(byte[] bytes)
        => Encoding.UTF8.GetString(bytes);

    private static string ToDataUrl(byte[] bytes, string? mediaType)
        => $"data:{mediaType ?? "application/octet-stream"};base64,{Convert.ToBase64String(bytes)}";

    private static Dictionary<string, JsonElement> ToJsonElementDictionary(Dictionary<string, object?> source)
        => source
            .Where(pair => pair.Value is not null)
            .ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value, JsonSerializerOptions.Web));

    private sealed class AnthropicMessagesFileEnrichmentState
    {
        public Dictionary<int, MessageContentBlock> SourceBlocks { get; } = [];

        public HashSet<string> SeenFileIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int NextSyntheticIndex { get; set; } = SyntheticAnthropicFileStreamIndexBase;
    }

    private sealed class AnthropicFileDownloadResult
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string? SourceToolCallId { get; init; }

        public string? SourceToolResultType { get; init; }

        public string FileId { get; init; } = string.Empty;

        public string Filename { get; init; } = string.Empty;

        public string MediaType { get; init; } = "application/octet-stream";

        public string CanonicalUrl { get; init; } = string.Empty;

        public byte[] Bytes { get; init; } = [];

        public string DataUrl { get; init; } = string.Empty;

        public JsonElement Input { get; init; }

        public CallToolResult Output { get; set; } = new();

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];

        public bool IsError { get; init; }

        public AnthropicFileDownloadError? Error { get; init; }
    }

    private sealed record AnthropicFileDownloadError(
        string Type,
        HttpStatusCode? StatusCode,
        string Message);

    private static Dictionary<string, Dictionary<string, object>>? TryGetAnthropicProviderMetadata(
        Dictionary<string, object?>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue("messages.provider.metadata", out var value)
            || value is null)
        {
            return null;
        }

        if (value is Dictionary<string, Dictionary<string, object>> typed)
            return typed;

        try
        {
            return value is JsonElement json
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(json.GetRawText(), JsonSerializerOptions.Web)
                : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }
}
