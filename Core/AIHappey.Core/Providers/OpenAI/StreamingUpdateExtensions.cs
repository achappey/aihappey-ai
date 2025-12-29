using OpenAI.Responses;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using System.Text.Json;
using OpenAI.Containers;
using ModelContextProtocol.Protocol;
using System.Net.Mime;
using Microsoft.AspNetCore.StaticFiles;

namespace AIHappey.Core.Providers.OpenAI;

public static class StreamingUpdateExtensions
{
    private static readonly Dictionary<string, (string callId, string name, string args)> _toolCallArgs = [];

    public static async IAsyncEnumerable<UIMessagePart> ToStreamingResponseUpdate(this StreamingResponseUpdate update,
        ContainerClient openAIFileClient, object? StructuredOutputs = null)
    {

        if (update is StreamingResponseErrorUpdate streamingResponseErrorUpdate)
        {
            yield return streamingResponseErrorUpdate.Message.ToErrorUIPart();
        }

        if (update is StreamingResponseCreatedUpdate)
        {
            yield return new StartStepUIPart();
        }

        if (update is StreamingResponseOutputTextDeltaUpdate deltaUpdate)
        {

            yield return new TextDeltaUIMessageStreamPart
            {
                Id = deltaUpdate.ItemId,
                Delta = deltaUpdate.Delta
            };
        }

        if (update is StreamingResponseTextAnnotationAddedUpdate streamingResponseTextAnnotationAddedUpdate)
        {
            await foreach (var part in streamingResponseTextAnnotationAddedUpdate.GetSourceUiPartsFromAnnotation(openAIFileClient))
            {
                yield return part; // only yields when it's a valid container_file_citation
            }
        }

        if (update is StreamingResponseOutputItemDoneUpdate streamingResponseOutputItemDoneUpdate)
        {
            if (streamingResponseOutputItemDoneUpdate.Item
                is ReasoningResponseItem reasoningResponseItem)
            {
                foreach (var reasoningItem in reasoningResponseItem.SummaryParts
                    .OfType<ReasoningSummaryTextPart>())
                {

                    yield return new ReasoningStartUIPart()
                    {
                        Id = reasoningResponseItem.Id
                    };

                    yield return new ReasoningDeltaUIPart()
                    {
                        Delta = reasoningItem.Text,
                        Id = reasoningResponseItem.Id
                    };

                    yield return new ReasoningEndUIPart()
                    {
                        Id = reasoningResponseItem.Id,
                        ProviderMetadata = !string.IsNullOrEmpty(reasoningResponseItem.EncryptedContent)
                                ? new Dictionary<string, object> { { "signature", reasoningResponseItem.EncryptedContent } }
                                    .ToProviderMetadata()
                                : null
                    };
                }
            }

            if (streamingResponseOutputItemDoneUpdate.Item
               is ImageGenerationCallResponseItem imageGenerationCallResponseItem)
            {
                yield return new ToolOutputAvailablePart()
                {
                    ToolCallId = imageGenerationCallResponseItem.Id,
                    Output = new CallToolResult()
                    {
                        Content = [imageGenerationCallResponseItem.ToImageContentBlock()]
                    },
                    ProviderExecuted = true
                };

                yield return imageGenerationCallResponseItem.ToFileUIPart();
            }

            if (streamingResponseOutputItemDoneUpdate.Item
                          is FileSearchCallResponseItem fileSearchCallResponseItem)
            {
                yield return new ToolOutputAvailablePart()
                {
                    ToolCallId = fileSearchCallResponseItem.Id,
                    Output = new CallToolResult()
                    {
                        Content = ["Results not available".ToTextContentBlock()],
                    },
                    ProviderExecuted = true
                };
            }

            if (streamingResponseOutputItemDoneUpdate.Item
               is CodeInterpreterCallResponseItem codeInterpreterCallResponseItem)
            {
                List<ContentBlock> content =
                [
                    ..codeInterpreterCallResponseItem.Outputs.SelectMany(o => o switch
                    {
                        CodeInterpreterCallLogsOutput l =>
                            [l.Logs.ToTextContentBlock()],
                        CodeInterpreterCallImageOutput i =>
                            [new ImageContentBlock {
                                MimeType = MediaTypeNames.Image.Png,
                                Data = i.ImageUri.ToString() }],
                        _ => Array.Empty<ContentBlock>()
                    })
                ];

                content.Add(JsonSerializer.Serialize(new
                {
                    codeInterpreterCallResponseItem.ContainerId,
                }, JsonSerializerOptions.Web)
                .ToTextContentBlock());

                /*    var callLogs = codeInterpreterCallResponseItem.Outputs
                        .OfType<CodeInterpreterCallLogsOutput>()
                        .Where(a => !string.IsNullOrEmpty(a.Logs))
                        .Select(a => a.Logs).ToList() ?? [];*/

                //    var files2 = openAIFileClient.GetContainerFiles(codeInterpreterCallResponseItem.ContainerId);
                /*   var files = openAIFileClient.GetContainerFilesAsync(codeInterpreterCallResponseItem.ContainerId).ToBlockingEnumerable().ToList();


                   foreach (var file in files.Where(a => callLogs.Any(z => z?.Contains($"'{a.Path}'") == true)))
                   {
                       var url = $"https://api.openai.com/v1/containers/{codeInterpreterCallResponseItem.ContainerId}/files/{file.Id}/content";

                       yield return new SourceUIPart
                       {
                           Url = url,
                           Title = file.Path,
                           SourceId = url
                       };

                       var outputFileContent = await openAIFileClient.DownloadContainerFileAsync(codeInterpreterCallResponseItem.ContainerId,
                           file.Id);

                       var provider = new FileExtensionContentTypeProvider();

                       if (!provider.TryGetContentType(file.Path, out var contentType))
                       {
                           // default/fallback
                           contentType = "application/octet-stream";
                       }

                       yield return outputFileContent.Value.ToArray().ToFileUIPart(contentType);

                   }*/

                yield return new ToolOutputAvailablePart()
                {
                    ToolCallId = codeInterpreterCallResponseItem.Id,
                    Output = new CallToolResult()
                    {
                        Content = content.Count != 0
                        ? content
                        : ["Output not available".ToTextContentBlock()]
                    },
                    ProviderExecuted = true
                };
            }
        }

        if (update is StreamingResponseWebSearchCallCompletedUpdate streamingResponseWebSearchCallCompletedUpdate)
        {
            yield return new ToolOutputAvailablePart()
            {
                ToolCallId = streamingResponseWebSearchCallCompletedUpdate.ItemId,
                Output = new { },
                ProviderExecuted = true
            };
        }

        if (update is StreamingResponseWebSearchCallInProgressUpdate streamingResponseWebSearchCallInProgressUpdate)
        {
            yield return ToolCallPart.CreateProviderExecuted(streamingResponseWebSearchCallInProgressUpdate.ItemId, "web_search", new { });
        }

        if (update is StreamingResponseImageGenerationCallInProgressUpdate streamingResponseImageGenerationCallInProgressUpdate)
        {
            yield return ToolCallPart.CreateProviderExecuted(streamingResponseImageGenerationCallInProgressUpdate.ItemId, "image_generation", new { });
        }

        if (update is StreamingResponseFileSearchCallInProgressUpdate streamingResponseFileSearchCallInProgressUpdate)
        {
            yield return ToolCallPart.CreateProviderExecuted(streamingResponseFileSearchCallInProgressUpdate.ItemId, "file_search", new { });
        }

        if (update is StreamingResponseContentPartDoneUpdate streamingResponseContentPartDoneUpdate)
        {
            var dsds = streamingResponseContentPartDoneUpdate.Part;
        }

        if (update is StreamingResponseCodeInterpreterCallCompletedUpdate streamingResponseCodeInterpreterCallCompletedUpdate)
        {
            var dsds = streamingResponseCodeInterpreterCallCompletedUpdate.ItemId;
        }


        if (update is StreamingResponseOutputTextDoneUpdate streamingResponseOutputTextDone)
        {

            yield return streamingResponseOutputTextDone.ItemId.ToTextEndUIMessageStreamPart();

            if (StructuredOutputs != null)
            {
                var resultObject = JsonSerializer.Deserialize<object>(streamingResponseOutputTextDone.Text);

                if (resultObject != null)
                    yield return new DataUIPart()
                    {
                        Type = "data-" + "unknown",
                        Data = resultObject
                    };
            }
        }

        if (update is StreamingResponseCodeInterpreterCallCodeDoneUpdate streamingResponseCodeInterpreterCallCodeDoneUpdate)
        {
            yield return "\"}".ToToolCallDeltaPart(streamingResponseCodeInterpreterCallCodeDoneUpdate.ItemId);

            yield return streamingResponseCodeInterpreterCallCodeDoneUpdate.ToToolCallDeltaPart();
        }

        if (update is StreamingResponseCodeInterpreterCallCodeDeltaUpdate streamingResponseCodeInterpreterCallCodeDeltaUpdate)
        {
            yield return streamingResponseCodeInterpreterCallCodeDeltaUpdate.ToToolCallDeltaPart();
        }

        if (update is StreamingResponseCodeInterpreterCallInProgressUpdate streamingResponseCodeInterpreterCallInProgressUpdate)
        {
            yield return ToolCallStreamingStartPart.CreateProviderExecuted(
                                  streamingResponseCodeInterpreterCallInProgressUpdate.ItemId,
                                  Constants.CodeInterpreter);

            yield return "{\"code\": \"".ToToolCallDeltaPart(streamingResponseCodeInterpreterCallInProgressUpdate.ItemId);
        }

        if (update is StreamingResponseOutputItemAddedUpdate streamingResponseOutputItemAddedUpdate)
        {
            if (streamingResponseOutputItemAddedUpdate.Item
                is FunctionCallResponseItem functionCallResponseItem)
            {
                if (!_toolCallArgs.ContainsKey(functionCallResponseItem.Id))
                    _toolCallArgs[functionCallResponseItem.Id] =
                        (callId: functionCallResponseItem.CallId,
                        name: functionCallResponseItem.FunctionName,
                        args: "");

                yield return new ToolCallStreamingStartPart
                {
                    ToolCallId = functionCallResponseItem.CallId,
                    ToolName = functionCallResponseItem.FunctionName,
                };
            }
            else if (streamingResponseOutputItemAddedUpdate.Item
                is MessageResponseItem messageResponseItem)
            {
                yield return messageResponseItem.Id.ToTextStartUIMessageStreamPart();

                foreach (var annotationItem in messageResponseItem.Content.FirstOrDefault()?.OutputTextAnnotations ?? [])
                {
                    if (annotationItem is UriCitationMessageAnnotation uriCitationMessageAnnotation)
                    {
                        yield return new SourceUIPart
                        {
                            Url = uriCitationMessageAnnotation.Uri.ToString(),
                            Title = uriCitationMessageAnnotation.Title,
                            SourceId = uriCitationMessageAnnotation.Uri.ToString(),
                            ProviderMetadata = new Dictionary<string, object>()
                            {
                                { "Type", nameof(UriCitationMessageAnnotation) },
                                { "StartIndex", uriCitationMessageAnnotation.StartIndex! },
                                { "EndIndex", uriCitationMessageAnnotation.EndIndex! }
                            }
                        };
                    }
                    else if (annotationItem is FileCitationMessageAnnotation fileCitationMessageAnnotation)
                    {

                        yield return new SourceUIPart
                        {
                            Url = Constants.OpenAIFilesAPI + fileCitationMessageAnnotation.FileId.ToString(),
                            Title = fileCitationMessageAnnotation.FileId,
                            SourceId = Constants.OpenAIFilesAPI + fileCitationMessageAnnotation.FileId.ToString(),
                            ProviderMetadata = new Dictionary<string, object>()
                            {
                                { "Type", nameof(FileCitationMessageAnnotation) },
                                { "FileId", fileCitationMessageAnnotation.FileId },
                                { "Filename", fileCitationMessageAnnotation.Filename },
                                { "Index", fileCitationMessageAnnotation.Index }
                            }
                        };
                    }
                    else if (annotationItem is FilePathMessageAnnotation filePathMessageAnnotation)
                    {


                        yield return new SourceUIPart
                        {
                            Url = Constants.OpenAIFilesAPI + filePathMessageAnnotation.FileId.ToString(),
                            Title = filePathMessageAnnotation.FileId,
                            SourceId = Constants.OpenAIFilesAPI + filePathMessageAnnotation.FileId.ToString(),
                            ProviderMetadata = new Dictionary<string, object>()
                            {
                                { "Type", nameof(FilePathMessageAnnotation) },
                                { "FileId", filePathMessageAnnotation.FileId },
                                { "Index", filePathMessageAnnotation.Index }
                            }

                        };
                    }
                }
            }
        }

        // Accumulate tool call args deltas
        if (update is StreamingResponseFunctionCallArgumentsDeltaUpdate argsDeltaUpdate)
        {
            if (_toolCallArgs.ContainsKey(argsDeltaUpdate.ItemId))
            {
                var entry = _toolCallArgs[argsDeltaUpdate.ItemId];
                entry.args += argsDeltaUpdate.Delta;
                _toolCallArgs[argsDeltaUpdate.ItemId] = entry;

                yield return argsDeltaUpdate.ToToolCallDeltaPart(entry.callId);
            }
        }

        // Emit part when done, with concatenated arguments
        if (update is StreamingResponseFunctionCallArgumentsDoneUpdate argsDoneUpdate)
        {
            string args;
            string callId = null!;
            string toolName = null!;
            if (_toolCallArgs.TryGetValue(argsDoneUpdate.ItemId, out var tuple))
            {
                args = tuple.args;
                callId = tuple.callId;
                toolName = tuple.name;
                _toolCallArgs.Remove(argsDoneUpdate.ItemId);
            }
            else
            {
                // Fallback if no deltas were accumulated
                args = argsDoneUpdate.FunctionArguments?.ToString() ?? "";
            }

            yield return new ToolCallPart
            {
                ToolCallId = callId,
                ToolName = toolName,
                Input = JsonSerializer.Deserialize<object>(args)!
            };

            yield return new ToolApprovalRequestPart
            {
                ToolCallId = callId,
                ApprovalId = Guid.NewGuid().ToString(),
            };
        }

        if (update is StreamingResponseCompletedUpdate completedUpdate)
        {

            yield return "stop".ToFinishUIPart(
                completedUpdate.Response.Model,
                completedUpdate.Response.Usage.OutputTokenCount,
                completedUpdate.Response.Usage.InputTokenCount,
                completedUpdate.Response.Usage.TotalTokenCount,
                temperature: completedUpdate.Response.Temperature,
                reasoningTokens: completedUpdate.Response.Usage.OutputTokenDetails.ReasoningTokenCount
            );
        }
    }
}
