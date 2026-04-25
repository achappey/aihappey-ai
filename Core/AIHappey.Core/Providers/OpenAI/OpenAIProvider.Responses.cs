using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Streaming;
using ModelContextProtocol.Protocol;
using OpenAI.Containers;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;
        options.ContextManagement ??= options.Metadata
            .GetProviderOption<JsonElement[]>(GetIdentifier(), "context_management");

        this.SetDefaultResponseProperties(options);

        var response = await _client.GetResponses(
                   options,
                   providerId: GetIdentifier(),
                   ct: cancellationToken);

        response = await EnrichResponseResultWithContainerFilesAsync(response, cancellationToken);

        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? options.Model
            : response.Model;
        var effectiveServiceTier = string.IsNullOrWhiteSpace(response.ServiceTier)
            ? options.ServiceTier
            : response.ServiceTier;

        var pricing = OpenAITieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
            response.Usage,
            response.Metadata,
            pricing);

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

        options.ParallelToolCalls ??= true;

        this.SetDefaultResponseProperties(options);

        var enrichmentState = new OpenAiResponseStreamEnrichmentState();

        await foreach (var update in _client.GetResponsesUpdates(options,
            providerId: GetIdentifier(),
            ct: cancellationToken))
        {
            var enrichedUpdates = await EnrichResponseStreamPartWithContainerFilesAsync(
                update,
                enrichmentState,
                cancellationToken);

            foreach (var enrichedUpdate in enrichedUpdates)
            {
                if (enrichedUpdate is ResponseCompleted completed)
                {
                    var effectiveModelId = string.IsNullOrWhiteSpace(completed.Response.Model)
                        ? options.Model
                        : completed.Response.Model;
                    var effectiveServiceTier = string.IsNullOrWhiteSpace(completed.Response.ServiceTier)
                        ? options.ServiceTier
                        : completed.Response.ServiceTier;
                    var pricing = OpenAITieredPricingResolver.Resolve(
                        effectiveModelId,
                        effectiveServiceTier,
                        ModelCostMetadataEnricher.GetTotalTokens(completed.Response.Usage));

                    completed.Response.Metadata = ModelCostMetadataEnricher.AddCostFromUsage(
                        completed.Response.Usage,
                        completed.Response.Metadata,
                        pricing);
                }

                yield return enrichedUpdate;
            }
        }
    }

    private async Task<ResponseResult> EnrichResponseResultWithContainerFilesAsync(
        ResponseResult response,
        CancellationToken cancellationToken)
    {
        var responseOutput = response.Output?.ToList() ?? [];
        if (responseOutput.Count == 0)
            return response;

        var enrichedOutput = new List<object>(responseOutput.Count);
        var seenCitations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputItem in responseOutput)
        {
            enrichedOutput.Add(outputItem);

            var enrichedItems = await CreateResponseContainerFileArtifactsAsync(
                outputItem,
                seenCitations,
                cancellationToken);

            if (enrichedItems.Count > 0)
                enrichedOutput.AddRange(enrichedItems);
        }

        response.Output = enrichedOutput;
        return response;
    }

    private async Task<List<ResponseStreamPart>> EnrichResponseStreamPartWithContainerFilesAsync(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state,
        CancellationToken cancellationToken)
    {
        var parts = new List<ResponseStreamPart> { update };

        if (update is not ResponseOutputTextAnnotationAdded annotationAdded
            || !string.Equals(annotationAdded.Annotation.Type, "container_file_citation", StringComparison.OrdinalIgnoreCase))
        {
            return parts;
        }

        var download = await TryDownloadContainerCitationAsync(
            annotationAdded.Annotation,
            state.SeenCitationKeys,
            cancellationToken);

        if (download is null)
            return parts;

        var syntheticOutputIndex = state.NextOutputIndex++;

        parts.Add(new ResponseOutputItemAdded
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = new ResponseStreamItem
            {
                Id = download.ToolCallId,
                Type = "custom_tool_call",
                Name = DownloadFileToolName,
                Status = "in_progress",
                AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
                {
                    ["provider_executed"] = true,
                    ["provider_metadata"] = download.ProviderMetadata
                })
            }
        });

        parts.Add(new ResponseUnknownEvent
        {
            SequenceNumber = state.NextSequenceNumber++,
            Type = "response.custom_tool_call.input",
            Data = ToJsonElementDictionary(new Dictionary<string, object?>
            {
                ["output_index"] = syntheticOutputIndex,
                ["item_id"] = download.ToolCallId,
                ["tool_name"] = DownloadFileToolName,
                ["title"] = DownloadFileToolName,
                ["provider_executed"] = true,
                ["provider_metadata"] = download.ProviderMetadata,
                ["input"] = download.Input
            })
        });

        parts.Add(new ResponseUnknownEvent
        {
            SequenceNumber = state.NextSequenceNumber++,
            Type = "response.custom_tool_call.output",
            Data = ToJsonElementDictionary(new Dictionary<string, object?>
            {
                ["output_index"] = syntheticOutputIndex,
                ["item_id"] = download.ToolCallId,
                ["tool_name"] = DownloadFileToolName,
                ["provider_executed"] = true,
                ["provider_metadata"] = download.ProviderMetadata,
                ["output"] = CreateDownloadToolCallResult(download)
            })
        });

        parts.Add(new ResponseOutputItemDone
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = new ResponseStreamItem
            {
                Id = download.ToolCallId,
                Type = "custom_tool_call",
                Name = DownloadFileToolName,
                Status = "completed",
                AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
                {
                    ["provider_executed"] = true,
                    ["provider_metadata"] = download.ProviderMetadata,
                    ["input"] = download.Input,
                    ["output"] = CreateDownloadToolCallResult(download)
                })
            }
        });

        return parts;
    }

    private async Task<List<object>> CreateResponseContainerFileArtifactsAsync(
        object outputItem,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var result = new List<object>();
        var map = TryGetJsonElementMap(outputItem);
        if (map is null)
            return result;

        var itemType = map.TryGetString("type") ?? string.Empty;
        var itemRole = map.TryGetString("role") ?? "assistant";

        if (!string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(itemRole, "assistant", StringComparison.OrdinalIgnoreCase)
            || !map.TryGetValue("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var contentPart in contentElement.EnumerateArray())
        {
            if (!contentPart.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "output_text", StringComparison.OrdinalIgnoreCase)
                || !contentPart.TryGetProperty("annotations", out var annotationsElement)
                || annotationsElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var annotation in annotationsElement.EnumerateArray())
            {
                var download = await TryDownloadContainerCitationAsync(annotation, seenCitations, cancellationToken);
                if (download is null)
                    continue;

                result.Add(new
                {
                    type = "custom_tool_call",
                    id = download.ToolCallId,
                    status = "completed",
                    name = DownloadFileToolName,
                    input = download.Input,
                    output = CreateDownloadToolCallResult(download),
                    provider_executed = true,
                    provider_metadata = download.ProviderMetadata
                });

                result.Add(new
                {
                    type = "message",
                    role = "assistant",
                    status = "completed",
                    content = new object[]
                    {
                        new InputFilePart
                        {
                            FileId = download.FileId,
                            FileUrl = download.CanonicalUrl,
                            FileData = download.DataUrl,
                            Filename = download.Filename
                        }
                    }
                });
            }
        }

        return result;
    }

    private async Task<OpenAiContainerCitationDownload?> TryDownloadContainerCitationAsync(
        object annotation,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var data = TryGetJsonElementMap(annotation);
        if (data is null)
            return null;

        var citationType = data.TryGetString("type");
        if (!string.Equals(citationType, "container_file_citation", StringComparison.OrdinalIgnoreCase))
            return null;

        var containerId = data.TryGetString("container_id");
        var fileId = data.TryGetString("file_id");
        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(fileId))
            return null;

        var citationKey = $"{containerId}:{fileId}";
        if (!seenCitations.Add(citationKey))
            return null;

        try
        {
            var filename = data.TryGetString("filename") ?? data.TryGetString("title");
            var canonicalOpenAiFileUrl = $"https://api.openai.com/v1/containers/{containerId}/files/{fileId}/content";

            var containerClient = new ContainerClient(GetKey());
            var content = await containerClient.DownloadContainerFileAsync(containerId, fileId, cancellationToken);
            var bytes = content.Value.ToArray();

            var resolvedFilename = string.IsNullOrWhiteSpace(filename) ? $"{fileId}.bin" : filename;
            var contentTypeProvider = new FileExtensionContentTypeProvider();
            var mediaType = contentTypeProvider.TryGetContentType(resolvedFilename, out var detected)
                ? detected
                : "application/octet-stream";

            var providerMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = BuildContainerCitationMetadata(data, containerId, fileId, resolvedFilename, canonicalOpenAiFileUrl)
            };

            return new OpenAiContainerCitationDownload
            {
                ToolCallId = Guid.NewGuid().ToString("n"),
                ContainerId = containerId,
                FileId = fileId,
                Filename = resolvedFilename,
                CanonicalUrl = canonicalOpenAiFileUrl,
                MediaType = mediaType,
                Bytes = bytes,
                DataUrl = ToDataUrl(bytes, mediaType),
                ProviderMetadata = providerMetadata,
                Input = JsonSerializer.SerializeToElement(new
                {
                    file_id = fileId,
                    file_name = resolvedFilename,
                    file_type = mediaType,
                    container_id = containerId,
                    openai_file_url = canonicalOpenAiFileUrl
                }, JsonSerializerOptions.Web)
            };
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

    private static CallToolResult CreateDownloadToolCallResult(OpenAiContainerCitationDownload download)
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
        => source.ToDictionary(
            pair => pair.Key,
            pair => JsonSerializer.SerializeToElement(pair.Value, JsonSerializerOptions.Web));

    private sealed class OpenAiContainerCitationDownload
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string ContainerId { get; init; } = string.Empty;

        public string FileId { get; init; } = string.Empty;

        public string Filename { get; init; } = string.Empty;

        public string CanonicalUrl { get; init; } = string.Empty;

        public string MediaType { get; init; } = "application/octet-stream";

        public byte[] Bytes { get; init; } = [];

        public string DataUrl { get; init; } = string.Empty;

        public JsonElement Input { get; init; }

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];
    }

    private sealed class OpenAiResponseStreamEnrichmentState
    {
        public HashSet<string> SeenCitationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int NextOutputIndex { get; set; } = 100_000;

        public int NextSequenceNumber { get; set; } = 1_000_000;
    }

    private const string DownloadFileToolName = "download_file";
}
