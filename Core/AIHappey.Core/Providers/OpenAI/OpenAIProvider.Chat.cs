using AIHappey.Common.Model;
using OpenAI.Files;
using Microsoft.AspNetCore.StaticFiles;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using AIHappey.Responses;
using System.Text.Json;
using OpenAI.Containers;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
         ChatRequest chatRequest,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model.Type)
        {
            case "image":
                await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "speech":
                await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
            case "video":
                await foreach (var p in this.StreamVideoAsync(chatRequest, cancellationToken))
                    yield return p;
                yield break;
        }

        ApplyAuthHeader();

        await foreach (var update in this.StreamResponsesAsync(
            chatRequest,
            CreateResponsesStreamRequestAsync,
            CreateResponsesStreamMappingOptions,
            PostProcessResponsesStreamPartAsync,
            cancellationToken))
        {
            yield return update;
        }
    }

    private async ValueTask<ResponseRequest> CreateResponsesStreamRequestAsync(
        ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        var metadata = chatRequest.GetProviderMetadata<OpenAiProviderMetadata>(GetIdentifier());
        var codeInterpreterFiles = await UploadCodeInterpreterFilesAsync(chatRequest, metadata, cancellationToken);
        var endUserId = _endUserIdResolver.Resolve(chatRequest);

        if (metadata?.Tools is { Length: > 0 })
        {
            metadata.Tools = ApplyCodeInterpreterFileIds(
                metadata.Tools,
                codeInterpreterFiles);
        }

        var request = chatRequest.ToResponsesRequest(GetIdentifier(), new ResponsesRequestMappingOptions
        {
            Instructions = metadata?.Instructions,
            InputImageDetail = metadata?.InputImageDetail,
            Store = false,
            ContextManagement = metadata?.ContextManagement,
            Reasoning = metadata?.Reasoning != null ? new Reasoning()
            {
                Effort = metadata?.Reasoning?.Effort,
                Summary = metadata?.Reasoning?.Summary
            } : null,
            ParallelToolCalls = metadata?.ParallelToolCalls,
            ServiceTier = metadata?.ServiceTier,
            Include = metadata?.Include?.ToList(),
            Metadata = new Dictionary<string, object?>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            },
            Tools = [.. chatRequest.Tools?.Select(a => a.ToResponseToolDefinition()) ?? []],
            ToolChoice = chatRequest.ToolChoice,
        });

        ApplyOpenAIRequestEnhancements(request, chatRequest, metadata, codeInterpreterFiles);
        return request;
    }

    private ResponsesStreamMappingOptions CreateResponsesStreamMappingOptions(ChatRequest chatRequest)
    {
        var providerTools = Array.Empty<ResponseToolDefinition>();
        var providerId = GetIdentifier();
        return new ResponsesStreamMappingOptions
        {
            StructuredOutputs = chatRequest.ResponseFormat,
            ProviderExecutedTools = [.. providerTools
                .Select(a => a.Extra?.TryGetValue("name", out var n) == true ? n.GetString() : null)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .OfType<string>()],
            ResolveToolTitle = toolName => chatRequest.Tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
            AnnotationMapper = (annotation, ct) => MapOpenAIAnnotationAsync(annotation, ct),
            OutputItemDoneMapper = (outputItemDone, context, ct) => MapOpenAIOutputItemDoneAsync(outputItemDone, providerId, ct),
            UnknownEventMapper = (unknown, context, ct) => MapOpenAIUnknownEventAsync(unknown, providerId, ct),
            FinishFactory = response => CreateOpenAIFinishPart(response)
        };
    }

    private async IAsyncEnumerable<UIMessagePart> MapOpenAIOutputItemDoneAsync(
        ResponseOutputItemDone outputItemDone,
        string providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.Equals(outputItemDone.Item.Type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            var metadata =
                JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(outputItemDone.Item.AdditionalProperties)
                ) ?? [];

            metadata["id"] = outputItemDone.Item.Id!;

            yield return new ReasoningStartUIPart
            {
                Id = outputItemDone.Item.Id!
            };

            var summaries =
                outputItemDone.Item.AdditionalProperties?
                    .TryGetValue("summary", out var summaryEl) == true &&
                summaryEl.ValueKind == JsonValueKind.Array
                    ? summaryEl.Deserialize<List<ResponseReasoningSummaryTextPart>>() ?? []
                    : [];

            yield return new ReasoningDeltaUIPart
            {
                Id = outputItemDone.Item.Id!,
                Delta = string.Join("\n\n", summaries?.Select(a => a.Text) ?? [])
            };


            yield return new ReasoningEndUIPart
            {
                Id = outputItemDone.Item.Id!,
                ProviderMetadata = new Dictionary<string, Dictionary<string, object>>
                {
                    [GetIdentifier()] = metadata
                },
            };
        }

        if (string.Equals(outputItemDone.Item.Type, "image_generation_call", StringComparison.OrdinalIgnoreCase))
        {
            var filePart = TryCreateOpenAIImageFilePart(providerId, outputItemDone.Item.AdditionalProperties);
            if (filePart != null)
                yield return filePart;
        }

        await Task.CompletedTask;
    }

    private async IAsyncEnumerable<UIMessagePart> MapOpenAIUnknownEventAsync(
        ResponseUnknownEvent unknown,
        string providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.Equals(unknown.Type, "response.image_generation_call.completed", StringComparison.OrdinalIgnoreCase))
        {
            var filePart = TryCreateOpenAIImageFilePart(providerId, unknown.Data);
            if (filePart != null)
                yield return filePart;
        }

        await Task.CompletedTask;
    }

    private static FileUIPart? TryCreateOpenAIImageFilePart(string providerId, Dictionary<string, JsonElement>? data)
    {
        var base64 = data.TryGetString("image_base64")
            ?? data.TryGetString("b64_json")
            ?? data.TryGetNestedString("result", "b64_json")
            ?? data.TryGetNestedString("result", "image_base64");

        if (!string.IsNullOrWhiteSpace(base64))
        {
            var mimeType = data.TryGetString("mime_type")
                ?? data.TryGetNestedString("result", "mime_type")
                ?? "image/png";

            return new FileUIPart
            {
                MediaType = mimeType,
                Url = base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? base64
                    : base64.ToDataUrl(mimeType)
            };
        }

        var url = data.TryGetString("image_url")
            ?? data.TryGetString("url")
            ?? data.TryGetString("result")
            ?? data.TryGetNestedString("result", "url")
            ?? data.TryGetNestedString("result", "image_url");

        if (string.IsNullOrWhiteSpace(url))
            return null;

        return new FileUIPart
        {
            MediaType = $"image/{data.TryGetString("output_format")}",
            Url = url,
            ProviderMetadata = new Dictionary<string, Dictionary<string, object>?>
            {
                [providerId] = JsonSerializer.Deserialize<Dictionary<string, object>>(
                    JsonSerializer.Serialize(data)
                )
            },
        };
    }

    private async IAsyncEnumerable<UIMessagePart> PostProcessResponsesStreamPartAsync(
        UIMessagePart part,
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (part is ToolCallStreamingStartPart toolCallStreamingStartPart)
            toolCallStreamingStartPart.Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolCallStreamingStartPart.ToolName)?.Title;

        if (part is ToolCallPart toolCallPart)
            toolCallPart.Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolCallPart.ToolName)?.Title;

        yield return part;
        await Task.CompletedTask;
    }

    private static void ApplyOpenAIRequestEnhancements(
        ResponseRequest request,
        ChatRequest chatRequest,
        OpenAiProviderMetadata? metadata,
        IReadOnlyCollection<string> codeInterpreterFiles)
    {
        request.MaxOutputTokens ??= chatRequest.MaxOutputTokens;
        request.Metadata ??= new Dictionary<string, object?>();

        if (metadata?.Truncation?.Equals("disabled", StringComparison.OrdinalIgnoreCase) == true)
            request.Truncation = TruncationStrategy.Disabled;
        else if (!string.IsNullOrWhiteSpace(metadata?.Truncation))
            request.Truncation = TruncationStrategy.Auto;
    }

    private async Task<IReadOnlyCollection<string>> UploadCodeInterpreterFilesAsync(
        ChatRequest chatRequest,
        OpenAiProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var lastUser = chatRequest.Messages.LastOrDefault(a => a.Role == Role.user);
        var mimeSet = OpenAIModelExtensions.CodeInterpreterMimeTypes;
        var allFileParts = (lastUser?.Parts ?? Enumerable.Empty<UIMessagePart>())
            .OfType<FileUIPart>()
            .Where(f => mimeSet.Contains(f.MediaType))
            .ToList();

        var hasCodeInterpreter = metadata?.Tools?
        .Any(tool =>
            tool.TryGetProperty("type", out var type) &&
            type.GetString() == "code_interpreter" &&
            tool.TryGetProperty("container", out var container) &&
            container.ValueKind != JsonValueKind.String
        ) ?? false;

        if (allFileParts.Count == 0 || !hasCodeInterpreter)
        {
            return [];
        }

        var fileClient = GetFileClient();
        List<string> codeInterpreterFiles = [];

        foreach (var file in allFileParts)
        {
            string dataUrl = file.Url;
            int commaIndex = dataUrl.IndexOf(',');
            if (commaIndex < 0)
                throw new FormatException("Invalid Data URI");

            string base64 = dataUrl[(commaIndex + 1)..];
            await using var ms = new MemoryStream(Convert.FromBase64String(base64));
            var provider = new FileExtensionContentTypeProvider();
            string extension = ".bin";
            var timestamp = DateTime.UtcNow.ToString("yyMMdd_HHmmss");
            foreach (var kvp in provider.Mappings)
            {
                if (string.Equals(kvp.Value, file.MediaType, StringComparison.OrdinalIgnoreCase))
                {
                    extension = kvp.Key;
                    break;
                }
            }

            var filename = $"{timestamp}{extension}";
            var result = await fileClient.UploadFileAsync(ms, filename, FileUploadPurpose.UserData, cancellationToken);
            codeInterpreterFiles.Add(result.Value.Id);
        }

        return codeInterpreterFiles;
    }
    private static JsonElement[] ApplyCodeInterpreterFileIds(
        JsonElement[] tools,
        IReadOnlyCollection<string> fileIds)
    {
        if (tools.Length == 0 || fileIds.Count == 0)
            return tools;

        var result = new JsonElement[tools.Length];

        for (int i = 0; i < tools.Length; i++)
        {
            var tool = tools[i];

            if (!tool.TryGetProperty("type", out var typeProp) ||
                typeProp.GetString() != "code_interpreter" ||
                !tool.TryGetProperty("container", out var container) ||
                container.ValueKind != JsonValueKind.Object)
            {
                result[i] = tool;
                continue;
            }

            var obj = JsonNode.Parse(tool.GetRawText())!.AsObject();

            obj["container"] ??= new JsonObject();
            obj["container"]!["file_ids"] = JsonSerializer.SerializeToNode(fileIds);

            result[i] = JsonSerializer.SerializeToElement(obj);
        }

        return result;
    }

    private async IAsyncEnumerable<UIMessagePart> MapOpenAIAnnotationAsync(
        ResponseStreamAnnotation annotation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var type = annotation.Type ?? (annotation.AdditionalProperties?.TryGetValue("type", out var typeValue) == true ? typeValue.GetString() : null);

        var providerMetadata = new Dictionary<string, Dictionary<string, object>>()
        {
            [GetIdentifier()] = JsonSerializer.Deserialize<Dictionary<string, object>>(
                                    JsonSerializer.Serialize(annotation.AdditionalProperties ?? [])
                            ) ?? []
        };

        if (string.Equals(type, "file_citation", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(type, "file_path", StringComparison.OrdinalIgnoreCase))
        {
            var fileId = annotation.AdditionalProperties.TryGetString("file_id");
            if (!string.IsNullOrWhiteSpace(fileId))
            {
                var url = $"https://api.openai.com/v1/files/{fileId}";

                yield return new SourceUIPart
                {
                    Url = url,
                    Title = annotation.AdditionalProperties.TryGetString("filename"),
                    SourceId = url,
                };
            }

            yield break;
        }

        if (string.Equals(type, "container_file_citation", StringComparison.OrdinalIgnoreCase))
        {
            var containerId = annotation.AdditionalProperties?.TryGetValue("container_id", out var containerIdValue) == true ? containerIdValue.GetString() : null;
            var fileId = annotation.AdditionalProperties?.TryGetValue("file_id", out var fileIdValue) == true ? fileIdValue.GetString() : null;
            var filename = annotation.AdditionalProperties?.TryGetValue("filename", out var filenameValue) == true ? filenameValue.GetString() : null;
            //REPLACE THI
            if (!string.IsNullOrWhiteSpace(containerId) && !string.IsNullOrWhiteSpace(fileId))
            {
                var containerClient = new ContainerClient(GetKey());


                var url = $"https://api.openai.com/v1/containers/{containerId}/files/{fileId}/content";

                yield return new SourceUIPart
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(filename) ? null : filename,
                    SourceId = url
                };

                yield return ToolCallPart.CreateProviderExecuted(fileId,
                    "download_container_file", new
                    {
                        containerId,
                        fileId
                    });

                var content = await containerClient.DownloadContainerFileAsync(containerId, fileId, cancellationToken);
                var provider = new FileExtensionContentTypeProvider();

                if (!provider.TryGetContentType(filename!, out var contentType))
                {
                    // default/fallback
                    contentType = "application/octet-stream";
                }

                yield return new ToolOutputAvailablePart()
                {
                    ToolCallId = fileId,
                    ProviderExecuted = true,
                    Output = new ModelContextProtocol.Protocol.CallToolResult()
                    {
                        Content = [new ModelContextProtocol.Protocol.EmbeddedResourceBlock() {
                        Resource = new ModelContextProtocol.Protocol.BlobResourceContents() {
                            Uri = $"file://{filename!}",
                            Blob = content.Value,
                            MimeType = contentType,
                        }
                      }]
                    }
                };

                var uiFile = content.Value.ToArray()
                                          .ToFileUIPart(contentType, new Dictionary<string, Dictionary<string, object>?>()
                          {
                            {"openai", new Dictionary<string, object>()
                                    {
                                        {"filename", filename!}
                                    }
                                }
                          });

                yield return uiFile;
            }

            yield break;
        }

        if (string.Equals(type, "url_citation", StringComparison.OrdinalIgnoreCase))
        {
            var url = annotation.AdditionalProperties?.TryGetValue("url", out var containerIdValue) == true ? containerIdValue.GetString() : null;
            var title = annotation.AdditionalProperties?.TryGetValue("title", out var fileIdValue) == true ? fileIdValue.GetString() : null;

            if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(title))
            {
                yield return new SourceUIPart
                {
                    Url = url,
                    Title = title,
                    SourceId = url,
                    ProviderMetadata = providerMetadata
                };
            }

            yield break;
        }

        await Task.CompletedTask;
        yield break;
    }

    private static FinishUIPart CreateOpenAIFinishPart(AIHappey.Responses.ResponseResult response)
    {
        var effectiveModelId = string.IsNullOrWhiteSpace(response.Model)
            ? string.Empty
            : response.Model;
        var effectiveServiceTier = response.ServiceTier;
        var pricing = OpenAITieredPricingResolver.Resolve(
            effectiveModelId,
            effectiveServiceTier,
            ModelCostMetadataEnricher.GetTotalTokens(response.Usage));

        return ModelCostMetadataEnricher.AddCost(
            "stop".ToFinishUIPart(
                response.Model,
                GetUsageValue(response.Usage, "output_tokens", "outputTokens") ?? 0,
                GetUsageValue(response.Usage, "input_tokens", "inputTokens") ?? 0,
                ModelCostMetadataEnricher.GetTotalTokens(response.Usage) ?? 0,
                response.Temperature,
                reasoningTokens: GetUsageValue(response.Usage, "reasoning_tokens", "reasoningTokens")),
            pricing);
    }

    private static int? GetUsageValue(object? usage, params string[] propertyNames)
    {
        if (usage == null)
            return null;

        try
        {
            var json = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            foreach (var name in propertyNames)
            {
                if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    return number;
            }

            if (json.TryGetProperty("output_tokens_details", out var outputDetails))
            {
                foreach (var name in propertyNames)
                {
                    if (outputDetails.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                        return number;
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
