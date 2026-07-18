using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using ModelContextProtocol.Protocol;
using OpenAI.Containers;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model.Type.Equals("transcription", StringComparison.OrdinalIgnoreCase))
        {
            var unified = await this.ExecuteUnifiedAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken);

            return unified.ToResponseResult();
        }

        if (model.Type.Equals("image"))
            return await this.ImageResponseAsync(options, cancellationToken);

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

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

        response.Metadata = AddOpenAIWebSearchCallCost(
            response.Metadata,
            CountCompletedWebSearchCalls(response.Output));

        return response;
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model.Type.Equals("transcription", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var streamEvent in this.StreamUnifiedAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken).WithCancellation(cancellationToken))
            {
                yield return streamEvent.ToResponseStreamPart();
            }

            yield break;
        }

        if (model.Type.Equals("image"))
        {
            await foreach (var part in this.ImageResponsesStreamingAsync(options, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return part;
            }

            yield break;
        }

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetKey());

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
                CaptureCompletedWebSearchCall(enrichedUpdate, enrichmentState);

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

                    completed.Response.Metadata = AddOpenAIWebSearchCallCost(
                        completed.Response.Metadata,
                        Math.Max(
                            enrichmentState.CompletedWebSearchCallCount,
                            CountCompletedWebSearchCalls(completed.Response.Output)));
                }

                yield return enrichedUpdate;
            }
        }
    }

    private static void CaptureCompletedWebSearchCall(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state)
    {
        if (update is not ResponseOutputItemDone done
            || !IsCompletedWebSearchCall(done.Item))
        {
            return;
        }

        var itemId = string.IsNullOrWhiteSpace(done.Item.Id)
            ? $"output_index:{done.OutputIndex}"
            : done.Item.Id;

        state.CompletedWebSearchCallIds.Add(itemId);
    }

    private static int CountCompletedWebSearchCalls(IEnumerable<object>? output)
    {
        if (output is null)
            return 0;

        var count = 0;
        var seenItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var outputItem in output)
        {
            var map = TryGetJsonElementMap(outputItem);
            if (map is null)
                continue;

            var itemType = map.TryGetString("type") ?? string.Empty;
            var itemStatus = map.TryGetString("status") ?? string.Empty;
            if (!string.Equals(itemType, "web_search_call", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(itemStatus, "completed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var itemId = map.TryGetString("id");
            if (string.IsNullOrWhiteSpace(itemId))
            {
                count++;
                continue;
            }

            if (seenItemIds.Add(itemId))
                count++;
        }

        return count;
    }

    private static bool IsCompletedWebSearchCall(ResponseStreamItem? item)
        => item is not null
           && string.Equals(item.Type, "web_search_call", StringComparison.OrdinalIgnoreCase)
           && string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase);



    private static Dictionary<string, object?>? TryConvertGatewayMetadata(object? gatewayObj)
    {
        if (gatewayObj is null)
            return null;

        try
        {
            return JsonSerializer.SerializeToElement(gatewayObj, JsonSerializerOptions.Web)
                .Deserialize<Dictionary<string, object?>>(JsonSerializerOptions.Web);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, JsonElement> ToJsonElementMap(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return element.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static decimal? TryGetDecimal(object? value)
    {
        return value switch
        {
            decimal decimalValue => decimalValue,
            double doubleValue => (decimal)doubleValue,
            float floatValue => (decimal)floatValue,
            int intValue => intValue,
            long longValue => longValue,
            JsonElement { ValueKind: JsonValueKind.Number } jsonNumber when jsonNumber.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonString when decimal.TryParse(jsonString.GetString(), out var decimalValue) => decimalValue,
            string stringValue when decimal.TryParse(stringValue, out var decimalValue) => decimalValue,
            _ => null
        };
    }

    private async Task<ResponseResult> EnrichResponseResultWithContainerFilesAsync(
        ResponseResult response,
        CancellationToken cancellationToken)
    {
        var responseOutput = response.Output?.ToList() ?? [];
        var responseContainerId = TryGetResponseContainerId(response);

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

            var imageResultDownloads = await CreateWebSearchImageResultArtifactsAsync(
                outputItem,
                seenCitations,
                cancellationToken);

            foreach (var download in imageResultDownloads)
                AddDownloadArtifacts(enrichedOutput, download);
        }

        if (seenCitations.Count == 0
            && !string.IsNullOrWhiteSpace(responseContainerId))
        {
            var fallbackDownloads = await TryDownloadAssistantContainerFilesAsync(
                responseContainerId,
                seenCitations,
                cancellationToken);

            foreach (var download in fallbackDownloads)
            {
                AddDownloadArtifacts(enrichedOutput, download);
            }
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

        CaptureResponseContainerId(update, state);

        var generatedImageUpload = await TryUploadGeneratedImageToResponseContainerAsync(
            update,
            state,
            cancellationToken);

        if (generatedImageUpload is not null)
        {
            AddSyntheticCustomToolCallParts(
                parts,
                state,
                generatedImageUpload.ToolCallId,
                UploadGeneratedImageToolName,
                generatedImageUpload.Input,
                CreateGeneratedImageUploadToolCallResult(generatedImageUpload),
                generatedImageUpload.ProviderMetadata);
        }

        var imageResultDownloads = await TryDownloadWebSearchImageResultsAsync(
            update,
            state.SeenCitationKeys,
            cancellationToken);

        foreach (var imageResultDownload in imageResultDownloads)
        {
            AddSyntheticCustomToolCallParts(
                parts,
                state,
                imageResultDownload.ToolCallId,
                DownloadFileToolName,
                imageResultDownload.Input,
                CreateDownloadToolCallResult(imageResultDownload),
                imageResultDownload.ProviderMetadata);
        }

        if (update is ResponseCompleted
            && state.SeenCitationKeys.Count == 0
            && !string.IsNullOrWhiteSpace(state.ResponseContainerId)
            && !state.AssistantContainerFallbackAttempted)
        {
            state.AssistantContainerFallbackAttempted = true;

            var fallbackDownloads = await TryDownloadAssistantContainerFilesAsync(
                state.ResponseContainerId,
                state.SeenCitationKeys,
                cancellationToken);

            foreach (var fallbackDownload in fallbackDownloads)
            {
                AddSyntheticCustomToolCallParts(
                    parts,
                    state,
                    fallbackDownload.ToolCallId,
                    DownloadFileToolName,
                    fallbackDownload.Input,
                    CreateDownloadToolCallResult(fallbackDownload),
                    fallbackDownload.ProviderMetadata);
            }
        }

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

        AddSyntheticCustomToolCallParts(
            parts,
            state,
            download.ToolCallId,
            DownloadFileToolName,
            download.Input,
            CreateDownloadToolCallResult(download),
            download.ProviderMetadata,
            syntheticOutputIndex);

        return parts;
    }

    private static void AddSyntheticCustomToolCallParts(
        List<ResponseStreamPart> parts,
        OpenAiResponseStreamEnrichmentState state,
        string toolCallId,
        string toolName,
        JsonElement input,
        CallToolResult output,
        Dictionary<string, Dictionary<string, object>> providerMetadata,
        int? outputIndex = null)
    {
        var syntheticOutputIndex = outputIndex ?? state.NextOutputIndex++;

        parts.Add(new ResponseOutputItemAdded
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = new ResponseStreamItem
            {
                Id = toolCallId,
                Type = "custom_tool_call",
                Name = toolName,
                Status = "in_progress",
                AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
                {
                    ["provider_executed"] = true,
                    ["provider_metadata"] = providerMetadata
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
                ["item_id"] = toolCallId,
                ["tool_name"] = toolName,
                ["title"] = toolName,
                ["provider_executed"] = true,
                ["provider_metadata"] = providerMetadata,
                ["input"] = input
            })
        });

        parts.Add(new ResponseUnknownEvent
        {
            SequenceNumber = state.NextSequenceNumber++,
            Type = "response.custom_tool_call.output",
            Data = ToJsonElementDictionary(new Dictionary<string, object?>
            {
                ["output_index"] = syntheticOutputIndex,
                ["item_id"] = toolCallId,
                ["tool_name"] = toolName,
                ["provider_executed"] = true,
                ["provider_metadata"] = providerMetadata,
                ["output"] = output
            })
        });

        parts.Add(new ResponseOutputItemDone
        {
            SequenceNumber = state.NextSequenceNumber++,
            OutputIndex = syntheticOutputIndex,
            Item = new ResponseStreamItem
            {
                Id = toolCallId,
                Type = "custom_tool_call",
                Name = toolName,
                Status = "completed",
                AdditionalProperties = ToJsonElementDictionary(new Dictionary<string, object?>
                {
                    ["provider_executed"] = true,
                    ["provider_metadata"] = providerMetadata,
                    ["input"] = input,
                    ["output"] = output
                })
            }
        });
    }

    private static void AddDownloadArtifacts(List<object> result, OpenAiContainerCitationDownload download)
    {
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

                AddDownloadArtifacts(result, download);
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

        return await TryDownloadContainerFileAsync(
            containerId,
            fileId,
            data.TryGetString("filename") ?? data.TryGetString("title"),
            JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web),
            seenCitations,
            cancellationToken);
    }

    private async Task<IReadOnlyList<OpenAiContainerCitationDownload>> TryDownloadAssistantContainerFilesAsync(
        string containerId,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var files = await TryListAssistantContainerFilesAsync(containerId, cancellationToken);
        if (files.Count == 0)
            return [];

        var downloads = new List<OpenAiContainerCitationDownload>(files.Count);

        foreach (var file in files)
        {
            var download = await TryDownloadContainerFileAsync(
                containerId,
                file.Id,
                string.IsNullOrWhiteSpace(file.Path) ? null : Path.GetFileName(file.Path),
                CreateContainerFileRawData(file),
                seenCitations,
                cancellationToken);

            if (download is not null)
                downloads.Add(download);
        }

        return downloads;
    }

    private async Task<IReadOnlyList<OpenAiContainerFileReference>> TryListAssistantContainerFilesAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        var result = new List<OpenAiContainerFileReference>();
        string? after = null;

        try
        {
            do
            {
                var requestUri = $"v1/containers/{Uri.EscapeDataString(containerId)}/files?limit=100&order=asc";
                if (!string.IsNullOrWhiteSpace(after))
                    requestUri += $"&after={Uri.EscapeDataString(after)}";

                var page = await _client.GetFromJsonAsync<OpenAiContainerFilesListResponse>(
                    requestUri,
                    JsonSerializerOptions.Web,
                    cancellationToken);

                if (page?.Data is null || page.Data.Count == 0)
                    break;

                foreach (var file in page.Data)
                {
                    if (string.IsNullOrWhiteSpace(file.Id)
                        || !string.Equals(file.Source, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(file);
                }

                after = page.LastId;
                if (string.IsNullOrWhiteSpace(after) || page.HasMore != true)
                    break;
            }
            while (true);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<OpenAiContainerCitationDownload>> CreateWebSearchImageResultArtifactsAsync(
        object outputItem,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var map = TryGetJsonElementMap(outputItem);
        if (map is null)
            return [];

        return await TryDownloadWebSearchImageResultsAsync(
            itemType: map.TryGetString("type"),
            itemStatus: map.TryGetString("status"),
            itemId: map.TryGetString("id") ?? string.Empty,
            outputIndex: null,
            additionalProperties: map,
            seenCitations: seenCitations,
            cancellationToken: cancellationToken);
    }

    private Task<List<OpenAiContainerCitationDownload>> TryDownloadWebSearchImageResultsAsync(
        ResponseStreamPart update,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        if (update is not ResponseOutputItemDone done)
            return Task.FromResult<List<OpenAiContainerCitationDownload>>([]);

        return TryDownloadWebSearchImageResultsAsync(
            itemType: done.Item.Type,
            itemStatus: done.Item.Status,
            itemId: done.Item.Id ?? string.Empty,
            outputIndex: done.OutputIndex,
            additionalProperties: done.Item.AdditionalProperties,
            seenCitations: seenCitations,
            cancellationToken: cancellationToken);
    }

    private async Task<List<OpenAiContainerCitationDownload>> TryDownloadWebSearchImageResultsAsync(
        string? itemType,
        string? itemStatus,
        string itemId,
        int? outputIndex,
        Dictionary<string, JsonElement>? additionalProperties,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(itemType, "web_search_call", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(itemStatus, "completed", StringComparison.OrdinalIgnoreCase)
            || additionalProperties?.TryGetValue("results", out var results) != true
            || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var downloads = new List<OpenAiContainerCitationDownload>();
        var imageIndex = 0;

        foreach (var result in results.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (result.ValueKind != JsonValueKind.Object
                || !string.Equals(result.TryGetString("type"), "image_result", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var download = await TryDownloadWebSearchImageResultAsync(
                itemId,
                outputIndex,
                imageIndex,
                result,
                seenCitations,
                cancellationToken);

            if (download is not null)
                downloads.Add(download);

            imageIndex++;
        }

        return downloads;
    }

    private async Task<OpenAiContainerCitationDownload?> TryDownloadWebSearchImageResultAsync(
        string itemId,
        int? outputIndex,
        int imageIndex,
        JsonElement imageResult,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        var imageUrl = imageResult.TryGetString("image_url");
        var thumbnailUrl = imageResult.TryGetString("thumbnail_url");
        var sourceWebsiteUrl = imageResult.TryGetString("source_website_url");
        var caption = imageResult.TryGetString("caption");

        var primaryUrl = IsHttpUrl(imageUrl) ? imageUrl : null;
        var fallbackUrl = IsHttpUrl(thumbnailUrl) ? thumbnailUrl : null;
        if (string.IsNullOrWhiteSpace(primaryUrl) && string.IsNullOrWhiteSpace(fallbackUrl))
            return null;

        var citationKey = $"web_search_image:{itemId}:{primaryUrl ?? fallbackUrl}";
        if (!seenCitations.Add(citationKey))
            return null;

        var downloaded = await TryDownloadImageResultBytesAsync(primaryUrl, fallbackUrl, cancellationToken);
        if (downloaded is null)
            return null;

        var mediaType = downloaded.MediaType;
        var filename = BuildWebSearchImageResultFilename(itemId, imageIndex, caption, mediaType, downloaded.Url);
        var fileId = $"{(string.IsNullOrWhiteSpace(itemId) ? "web_search" : itemId)}_image_{imageIndex}";
        var rawData = ToJsonElementMap(imageResult);

        var providerMetadata = new Dictionary<string, Dictionary<string, object>>
        {
            [GetIdentifier()] = BuildWebSearchImageResultMetadata(
                rawData,
                itemId,
                outputIndex,
                imageIndex,
                fileId,
                filename,
                mediaType,
                downloaded.Url,
                primaryUrl,
                fallbackUrl,
                sourceWebsiteUrl,
                caption)
        };

        return new OpenAiContainerCitationDownload
        {
            ToolCallId = Guid.NewGuid().ToString("n"),
            FileId = fileId,
            Filename = filename,
            CanonicalUrl = downloaded.Url,
            MediaType = mediaType,
            Bytes = downloaded.Bytes,
            DataUrl = ToDataUrl(downloaded.Bytes, mediaType),
            ProviderMetadata = providerMetadata,
            Input = JsonSerializer.SerializeToElement(new
            {
                source_item_id = itemId,
                output_index = outputIndex,
                result_index = imageIndex,
                image_url = primaryUrl,
                thumbnail_url = fallbackUrl,
                source_website_url = sourceWebsiteUrl,
                caption,
                file_name = filename,
                file_type = mediaType,
                url = downloaded.Url
            }, JsonSerializerOptions.Web)
        };
    }

    private async Task<OpenAiDownloadedWebSearchImage?> TryDownloadImageResultBytesAsync(
        string? primaryUrl,
        string? fallbackUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(primaryUrl)
            && await TryDownloadImageBytesAsync(primaryUrl, cancellationToken) is { } primaryDownload)
        {
            return primaryDownload;
        }

        if (!string.IsNullOrWhiteSpace(fallbackUrl)
            && !string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            return await TryDownloadImageBytesAsync(fallbackUrl, cancellationToken);
        }

        return null;
    }

    private async Task<OpenAiDownloadedWebSearchImage?> TryDownloadImageBytesAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0)
                return null;

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = GuessImageMediaType(url) ?? MediaTypeNames.Image.Png;

            return new OpenAiDownloadedWebSearchImage(url, mediaType, bytes);
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

    private static Dictionary<string, object> BuildWebSearchImageResultMetadata(
        Dictionary<string, JsonElement> rawData,
        string itemId,
        int? outputIndex,
        int imageIndex,
        string fileId,
        string filename,
        string mediaType,
        string downloadedUrl,
        string? imageUrl,
        string? thumbnailUrl,
        string? sourceWebsiteUrl,
        string? caption)
        => new()
        {
            ["type"] = "web_search_image_result",
            ["tool_name"] = DownloadFileToolName,
            ["name"] = DownloadFileToolName,
            ["download_tool"] = true,
            ["source_item_id"] = itemId,
            ["output_index"] = outputIndex ?? -1,
            ["result_index"] = imageIndex,
            ["file_id"] = fileId,
            ["filename"] = filename,
            ["media_type"] = mediaType,
            ["downloaded_url"] = downloadedUrl,
            ["image_url"] = imageUrl ?? string.Empty,
            ["thumbnail_url"] = thumbnailUrl ?? string.Empty,
            ["source_website_url"] = sourceWebsiteUrl ?? string.Empty,
            ["caption"] = caption ?? string.Empty,
            ["raw"] = JsonSerializer.SerializeToElement(rawData, JsonSerializerOptions.Web)
        };

    private static string BuildWebSearchImageResultFilename(
        string itemId,
        int imageIndex,
        string? caption,
        string mediaType,
        string url)
    {
        var baseName = string.IsNullOrWhiteSpace(caption)
            ? string.IsNullOrWhiteSpace(itemId) ? "openai-web-search-image" : itemId
            : caption;

        var safeName = string.Concat(baseName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "openai-web-search-image";

        if (safeName.Length > 80)
            safeName = safeName[..80].Trim('-');

        return $"{safeName}-{imageIndex + 1}{GuessImageExtension(mediaType, url)}";
    }

    private static string GuessImageExtension(string? mediaType, string? url)
    {
        var normalized = mediaType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            MediaTypeNames.Image.Jpeg or "image/jpg" => ".jpg",
            MediaTypeNames.Image.Png => ".png",
            MediaTypeNames.Image.Gif => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => GuessImageExtensionFromUrl(url) ?? ".png"
        };
    }

    private static string? GuessImageMediaType(string? url)
    {
        var extension = GuessImageExtensionFromUrl(url);
        return extension switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".png" => MediaTypeNames.Image.Png,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private static string? GuessImageExtensionFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".svg"
            ? extension
            : null;
    }

    private static bool IsHttpUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "http" or "https";

    private async Task<OpenAiContainerCitationDownload?> TryDownloadContainerFileAsync(
        string containerId,
        string fileId,
        string? filename,
        JsonElement rawData,
        HashSet<string> seenCitations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerId) || string.IsNullOrWhiteSpace(fileId))
            return null;

        var citationKey = $"{containerId}:{fileId}";
        if (!seenCitations.Add(citationKey))
            return null;

        try
        {
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
                [GetIdentifier()] = BuildContainerCitationMetadata(ToJsonElementMap(rawData), containerId, fileId, resolvedFilename, canonicalOpenAiFileUrl)
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

    private static JsonElement CreateContainerFileRawData(OpenAiContainerFileReference file)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "container_file_citation",
            ["container_id"] = file.ContainerId,
            ["file_id"] = file.Id,
            ["filename"] = string.IsNullOrWhiteSpace(file.Path) ? null : Path.GetFileName(file.Path),
            ["path"] = file.Path,
            ["source"] = file.Source,
            ["object"] = file.Object,
            ["bytes"] = file.Bytes,
            ["created_at"] = file.CreatedAt,
            ["raw"] = file.AdditionalProperties
        }, JsonSerializerOptions.Web);

    private void CaptureResponseContainerId(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state)
    {
        var containerId = update switch
        {
            ResponseCreated created => TryGetResponseContainerId(created.Response),
            ResponseInProgress inProgress => TryGetResponseContainerId(inProgress.Response),
            ResponseCompleted completed => TryGetResponseContainerId(completed.Response),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(containerId))
            state.ResponseContainerId = containerId;
    }

    private static string? TryGetResponseContainerId(ResponseResult? response)
    {
        if (response?.Tools is null)
            return null;

        foreach (var tool in response.Tools)
        {
            var toolMap = TryGetJsonElementMap(tool);
            if (toolMap is null)
                continue;

            var toolType = toolMap.TryGetString("type") ?? string.Empty;
            if (!string.Equals(toolType, "shell", StringComparison.OrdinalIgnoreCase)
                || !toolMap.TryGetValue("environment", out var environment)
                || environment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var environmentType = environment.TryGetString("type") ?? string.Empty;
            var containerId = environment.TryGetString("container_id");
            if (string.Equals(environmentType, "container_reference", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(containerId))
            {
                return containerId;
            }
        }

        return null;
    }

    private async Task<OpenAiGeneratedImageUpload?> TryUploadGeneratedImageToResponseContainerAsync(
        ResponseStreamPart update,
        OpenAiResponseStreamEnrichmentState state,
        CancellationToken cancellationToken)
    {
        if (update is not ResponseOutputItemDone done
            || !string.Equals(done.Item.Type, "image_generation_call", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(done.Item.Id)
            || string.IsNullOrWhiteSpace(state.ResponseContainerId)
            || !state.UploadedGeneratedImageItemIds.Add(done.Item.Id))
        {
            return null;
        }

        var itemProperties = done.Item.AdditionalProperties;
        if (itemProperties is null
            || !itemProperties.TryGetValue("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var base64Result = resultElement.GetString();
        if (string.IsNullOrWhiteSpace(base64Result))
            return null;

        try
        {
            var imageBytes = Convert.FromBase64String(base64Result);
            var outputFormat = itemProperties.TryGetValue("output_format", out var outputFormatElement)
                && outputFormatElement.ValueKind == JsonValueKind.String
                    ? outputFormatElement.GetString()
                    : null;
            var normalizedFormat = NormalizeImageOutputFormat(outputFormat);
            var mediaType = $"image/{normalizedFormat}";
            var filename = $"{done.Item.Id}.{normalizedFormat}";
            var containerId = state.ResponseContainerId;

            var containerClient = new ContainerClient(GetKey());
            var uploadResult = await OpenAIModelExtensions.UploadBytesMultipartAsync(
                containerClient,
                containerId,
                imageBytes,
                mediaType,
                options: new RequestOptions { CancellationToken = cancellationToken });

            var uploadMetadata = ExtractContainerFileMetadata(
                uploadResult,
                containerId,
                filename,
                imageBytes.Length,
                mediaType);

            var canonicalOpenAiFileUrl = string.IsNullOrWhiteSpace(uploadMetadata.FileId)
                ? $"https://api.openai.com/v1/containers/{containerId}/files"
                : $"https://api.openai.com/v1/containers/{containerId}/files/{uploadMetadata.FileId}/content";

            var providerMetadata = new Dictionary<string, Dictionary<string, object>>
            {
                [GetIdentifier()] = new Dictionary<string, object>
                {
                    ["type"] = "container_file_upload",
                    ["tool_name"] = UploadGeneratedImageToolName,
                    ["name"] = UploadGeneratedImageToolName,
                    ["upload_tool"] = true,
                    ["source_item_id"] = done.Item.Id,
                    ["container_id"] = containerId,
                    ["file_id"] = uploadMetadata.FileId,
                    ["filename"] = filename,
                    ["path"] = uploadMetadata.Path,
                    ["bytes"] = uploadMetadata.Bytes,
                    ["media_type"] = mediaType,
                    ["openai_file_url"] = canonicalOpenAiFileUrl,
                    ["raw"] = uploadMetadata.Raw
                }
            };

            return new OpenAiGeneratedImageUpload
            {
                ToolCallId = Guid.NewGuid().ToString("n"),
                SourceItemId = done.Item.Id,
                ContainerId = containerId,
                FileId = uploadMetadata.FileId,
                Filename = filename,
                Path = uploadMetadata.Path,
                CanonicalUrl = canonicalOpenAiFileUrl,
                MediaType = mediaType,
                Bytes = imageBytes,
                ProviderMetadata = providerMetadata,
                Input = JsonSerializer.SerializeToElement(new
                {
                    source_item_id = done.Item.Id,
                    file_name = filename,
                    file_type = mediaType,
                    container_id = containerId
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

    private static OpenAiContainerFileUploadMetadata ExtractContainerFileMetadata(
        ClientResult uploadResult,
        string fallbackContainerId,
        string fallbackFilename,
        int fallbackBytes,
        string mediaType)
    {
        try
        {
            var rawContent = uploadResult.GetRawResponse().Content.ToString();
            using var document = JsonDocument.Parse(rawContent);
            var root = document.RootElement.Clone();
            var fileId = root.TryGetString("id") ?? string.Empty;
            var path = root.TryGetString("path");
            var containerId = root.TryGetString("container_id") ?? fallbackContainerId;
            var bytes = root.TryGetNumber("bytes") ?? fallbackBytes;

            return new OpenAiContainerFileUploadMetadata(
                fileId,
                containerId,
                string.IsNullOrWhiteSpace(path) ? $"/mnt/data/{fallbackFilename}" : path,
                bytes,
                root);
        }
        catch
        {
            return new OpenAiContainerFileUploadMetadata(
                string.Empty,
                fallbackContainerId,
                $"/mnt/data/{fallbackFilename}",
                fallbackBytes,
                JsonSerializer.SerializeToElement(new
                {
                    container_id = fallbackContainerId,
                    filename = fallbackFilename,
                    bytes = fallbackBytes,
                    media_type = mediaType
                }, JsonSerializerOptions.Web));
        }
    }

    private static string NormalizeImageOutputFormat(string? outputFormat)
        => (outputFormat ?? "png").Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" => "jpeg",
            "jpeg" => "jpeg",
            "webp" => "webp",
            "gif" => "gif",
            "png" => "png",
            _ => "png"
        };

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

    private static CallToolResult CreateGeneratedImageUploadToolCallResult(OpenAiGeneratedImageUpload upload)
        => new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = string.IsNullOrWhiteSpace(upload.FileId)
                        ? $"Uploaded generated image to OpenAI container {upload.ContainerId} at {upload.Path}."
                        : $"Uploaded generated image to OpenAI container {upload.ContainerId} as {upload.FileId} at {upload.Path}."
                }
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                file_id = upload.FileId,
                filename = upload.Filename,
                media_type = upload.MediaType,
                container_id = upload.ContainerId,
                path = upload.Path,
                url = upload.CanonicalUrl,
                source_item_id = upload.SourceItemId
            }, JsonSerializerOptions.Web)
        };

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

    private sealed class OpenAiGeneratedImageUpload
    {
        public string ToolCallId { get; init; } = string.Empty;

        public string SourceItemId { get; init; } = string.Empty;

        public string ContainerId { get; init; } = string.Empty;

        public string FileId { get; init; } = string.Empty;

        public string Filename { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string CanonicalUrl { get; init; } = string.Empty;

        public string MediaType { get; init; } = "application/octet-stream";

        public byte[] Bytes { get; init; } = [];

        public JsonElement Input { get; init; }

        public Dictionary<string, Dictionary<string, object>> ProviderMetadata { get; init; } = [];
    }

    private sealed record OpenAiContainerFileUploadMetadata(
        string FileId,
        string ContainerId,
        string Path,
        int Bytes,
        JsonElement Raw);

    private sealed record OpenAiDownloadedWebSearchImage(
        string Url,
        string MediaType,
        byte[] Bytes);

    private sealed class OpenAiContainerFilesListResponse
    {
        [JsonPropertyName("data")]
        public List<OpenAiContainerFileReference> Data { get; init; } = [];

        [JsonPropertyName("has_more")]
        public bool? HasMore { get; init; }

        [JsonPropertyName("last_id")]
        public string? LastId { get; init; }
    }

    private sealed class OpenAiContainerFileReference
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("container_id")]
        public string? ContainerId { get; init; }

        [JsonPropertyName("object")]
        public string? Object { get; init; }

        [JsonPropertyName("bytes")]
        public int? Bytes { get; init; }

        [JsonPropertyName("created_at")]
        public long? CreatedAt { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
    }

    private sealed class OpenAiResponseStreamEnrichmentState
    {
        public HashSet<string> SeenCitationKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> UploadedGeneratedImageItemIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CompletedWebSearchCallIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int CompletedWebSearchCallCount => CompletedWebSearchCallIds.Count;

        public string? ResponseContainerId { get; set; }

        public bool AssistantContainerFallbackAttempted { get; set; }

        public int NextOutputIndex { get; set; } = 100_000;

        public int NextSequenceNumber { get; set; } = 1_000_000;
    }

    private const string DownloadFileToolName = "download_file";

    private const string UploadGeneratedImageToolName = "upload_generated_image";


}
