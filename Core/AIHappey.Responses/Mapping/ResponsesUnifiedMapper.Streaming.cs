using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvent(
        this ResponseStreamPart part,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        foreach (var envelope in ToUnifiedEnvelope(part, providerId))
        {
            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = envelope,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.type"] = part.Type
                }
            };
        }
    }

    public static ResponseStreamPart ToResponseStreamPart(this AIStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var envelope = streamEvent.Event;
        var kind = envelope.Type;
        var data = ToJsonMap(envelope.Data);

        return kind switch
        {
            "response.created" => new ResponseCreated
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Response = GetResponseResult(data, envelope)
            },
            "response.in_progress" => new ResponseInProgress
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Response = GetResponseResult(data, envelope)
            },
            "response.completed" => new ResponseCompleted
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Response = GetResponseResult(data, envelope)
            },
            "response.failed" => new ResponseFailed
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Response = GetResponseResult(data, envelope)
            },
            "error" => new ResponseError
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Message = GetValue<string>(data, "message") ?? "Unknown error",
                Param = GetValue<string>(data, "param") ?? string.Empty,
                Code = GetValue<string>(data, "code") ?? string.Empty
            },
            "response.output_text.delta" => new ResponseOutputTextDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Delta = GetValue<string>(data, "delta") ?? string.Empty,
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Outputindex = GetValue<int>(data, "output_index")
            },
            "response.output_text.done" => new ResponseOutputTextDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                Text = GetValue<string>(data, "text") ?? string.Empty,
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Outputindex = GetValue<int>(data, "output_index")
            },
            "response.output_item.added" => new ResponseOutputItemAdded
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                Item = GetResponseStreamItem(data)
            },
            "response.output_item.done" => new ResponseOutputItemDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                Item = GetResponseStreamItem(data)
            },
            "response.content_part.added" => new ResponseContentPartAdded
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Part = GetResponseStreamContentPart(data, "part")
            },
            "response.content_part.done" => new ResponseContentPartDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Part = GetResponseStreamContentPart(data, "part")
            },
            "response.output_text.annotation.added" => new ResponseOutputTextAnnotationAdded
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                AnnotationIndex = GetValue<int>(data, "annotation_index"),
                Annotation = GetResponseStreamAnnotation(data)
            },
            "response.reasoning_summary_text.delta" => new ResponseReasoningSummaryTextDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.reasoning_summary_text.done" => new ResponseReasoningSummaryTextDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Text = GetValue<string>(data, "text") ?? string.Empty
            },
            "response.reasoning_text.delta" => new ResponseReasoningTextDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.reasoning_text.done" => new ResponseReasoningTextDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Text = GetValue<string>(data, "text") ?? string.Empty
            },
            "response.refusal.delta" => new ResponseRefusalDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.refusal.done" => new ResponseRefusalDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Refusal = GetValue<string>(data, "refusal") ?? string.Empty
            },
            "response.reasoning_summary_part.added" => new ResponseReasoningSummaryPartAdded
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Part = GetResponseStreamContentPart(data, "part")
            },
            "response.reasoning_summary_part.done" => new ResponseReasoningSummaryPartDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                ContentIndex = GetValue<int>(data, "content_index"),
                Part = GetResponseStreamContentPart(data, "part")
            },
            "response.function_call_arguments.delta" => new ResponseFunctionCallArgumentsDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.function_call_arguments.done" => new ResponseFunctionCallArgumentsDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Arguments = GetValue<string>(data, "arguments") ?? "{}"
            },
            "response.mcp_call_arguments.delta" => new ResponseMcpCallArgumentsDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.mcp_call_arguments.done" => new ResponseMcpCallArgumentsDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Arguments = GetValue<string>(data, "arguments") ?? "{}"
            },
            "response.code_interpreter_call.in_progress" => new ResponseCodeInterpreterCallInProgress
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.code_interpreter_call_code.done" => new ResponseCodeInterpreterCallDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Code = GetValue<string>(data, "code") ?? string.Empty
            },
            "response.code_interpreter_call_code.delta" => new ResponseCodeInterpreterCallCodeDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty,
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.shell_call_command.added" => new ResponseShellCallCommandAdded
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                CommandIndex = GetValue<int>(data, "command_index"),
                Command = GetValue<string>(data, "command") ?? string.Empty
            },
            "response.shell_call_command.delta" => new ResponseShellCallCommandDelta
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                CommandIndex = GetValue<int>(data, "command_index"),
                Delta = GetValue<string>(data, "delta") ?? string.Empty
            },
            "response.shell_call_command.done" => new ResponseShellCallCommandDone
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                CommandIndex = GetValue<int>(data, "command_index"),
                Command = GetValue<string>(data, "command") ?? string.Empty
            },
            "response.file_search_call.completed" => new ResponseFileSearchCallCompleted
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.file_search_call.in_progress" => new ResponseFileSearchCallInProgress
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.file_search_call.searching" => new ResponseFileSearchCallSearching
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.web_search_call.completed" => new ResponseWebSearchCallCompleted
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.web_search_call.in_progress" => new ResponseWebSearchCallInProgress
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            "response.web_search_call.searching" => new ResponseWebSearchCallSearching
            {
                SequenceNumber = GetValue<int>(data, "sequence_number"),
                OutputIndex = GetValue<int>(data, "output_index"),
                ItemId = GetValue<string>(data, "item_id") ?? string.Empty
            },
            _ => new ResponseUnknownEvent
            {
                Type = kind,
                SequenceNumber = GetValue<int?>(data, "sequence_number"),
                Data = ToJsonElementMap(envelope.Data)
            }
        };
    }

    private static IEnumerable<AIEventEnvelope> ToUnifiedEnvelope(ResponseStreamPart part, string providerId)
    {
        switch (part)
        {
            case ResponseCreated created:
                yield return CreateLifecycleEnvelope(created.Type, created.SequenceNumber, created.Response, providerId);
                yield break;

            case ResponseInProgress inProgress:
                yield return CreateLifecycleEnvelope(inProgress.Type, inProgress.SequenceNumber, inProgress.Response, providerId);
                yield break;

            case ResponseCompleted completed:
                yield return CreateLifecycleEnvelope(completed.Type, completed.SequenceNumber, completed.Response, providerId);
                yield return CreateFinishEnvelope(completed.Type,
                    completed.SequenceNumber, completed.Response);
                yield break;

            case ResponseFailed failed:
                yield return CreateLifecycleEnvelope(failed.Type, failed.SequenceNumber, failed.Response, providerId);
                yield break;

            case ResponseReasoningSummaryPartAdded added:
                yield return CreateReasoningStartEnvelope(
                    providerId,
                    added.ItemId ?? string.Empty,
                    added.Part);
                yield break;

            case ResponseReasoningSummaryPartDone done:
                yield return CreateReasoningEndEnvelope(
                    providerId,
                    done.ItemId ?? string.Empty,
                    done.Part);
                yield break;

            case ResponseReasoningTextDelta responseReasoningTextDelta:
                yield return CreateReasoningDeltaEnvelope(responseReasoningTextDelta.ItemId, responseReasoningTextDelta.Delta);
                yield break;

            case ResponseOutputTextDelta delta:
                yield return CreateTextDeltaEnvelope(delta.ItemId, delta.Delta);
                yield break;

            case ResponseReasoningSummaryTextDelta delta:
                yield return CreateReasoningDeltaEnvelope(delta.ItemId, delta.Delta);
                yield break;
            case ResponseImageGenerationCallPartialImage responseImageGenerationCallPartialImage:

                var partial = responseImageGenerationCallPartialImage.PartialImageB64;
                var partialOutput = responseImageGenerationCallPartialImage.OutputFormat;

                var imageGenOutput = new CallToolResult()
                {
                    Content = [ImageContentBlock.FromBytes(
                                Convert.FromBase64String(partial),
                                $"image/{partialOutput}"
                            )]
                };

                yield return CreateToolOutputEnvelope(
                                        responseImageGenerationCallPartialImage.ItemId ?? string.Empty,
                                        imageGenOutput,
                                        preliminary: true,
                                        providerExecuted: true);
                yield break;
            case ResponseImageGenerationCallInProgress responseImageGenerationCallGenerating:
                yield return CreateToolInputEndEnvelope(
                                                     responseImageGenerationCallGenerating.ItemId ?? string.Empty,
                                                     "image_generation", new { },
                                                     providerExecuted: true);
                yield break;
            case ResponseContentPartAdded responseContentPartAdded:
                if (responseContentPartAdded.Part.Type == "reasoning_text")
                {
                    yield return CreateReasoningStartEnvelope(
                                       providerId,
                                       responseContentPartAdded.ItemId ?? string.Empty,
                                       responseContentPartAdded.Part);
                    yield break;
                }

                yield return CreateDataEnvelope(
                        part.Type,
                        JsonSerializer.SerializeToElement(part, part.GetType(), Json));

                yield break;

            case ResponseContentPartDone responseContentPartDone:
                if (responseContentPartDone.Part.Type == "reasoning_text")
                {
                    yield return CreateReasoningEndEnvelope(
                                       providerId,
                                       responseContentPartDone.ItemId ?? string.Empty,
                                       responseContentPartDone.Part);
                    yield break;
                }
                else if (responseContentPartDone.Part.Type == "output_text")
                {
                    foreach (var env in responseContentPartDone.Part.Annotations ?? [])
                    {
                        if (env.Type == "url_citation")
                        {
                            string? url = null;
                            string? title = null;

                            if (env.AdditionalProperties?.TryGetValue("url", out var u) == true)
                                url = u.ValueKind == JsonValueKind.String ? u.GetString() : u.ToString();

                            if (env.AdditionalProperties?.TryGetValue("title", out var t) == true)
                                title = t.ValueKind == JsonValueKind.String ? t.GetString() : t.ToString();

                            if (!string.IsNullOrEmpty(url))
                            {
                                yield return CreateSourceUrlEnvelope(
                                    responseContentPartDone.ItemId ?? string.Empty,
                                    url,
                                    title ?? responseContentPartDone.ItemId ?? url,
                                    env.Type
                                );
                            }
                        }
                        else if (env.Type == "file_citation")
                        {
                            var filename = TryGetAnnotationString(env, "filename");
                            var fileId = TryGetAnnotationString(env, "file_id");
                            var sourceLabel = filename ?? fileId ?? "file";
                            var index = TryGetAnnotationInt(env, "index");

                            yield return CreateSourceUrlEnvelope(
                                responseContentPartDone.ItemId ?? string.Empty,
                                $"file://{sourceLabel}",
                                sourceLabel,
                                env.Type,
                                fileId: fileId,
                                filename: filename,
                                providerMetadata: CreateProviderMetadata(providerId, new Dictionary<string, object?>
                                {
                                    ["index"] = index
                                })
                            );
                        }
                    }
                }

                yield return CreateDataEnvelope(
                         part.Type,
                         JsonSerializer.SerializeToElement(part, part.GetType(), Json));

                yield break;
            case ResponseOutputTextAnnotationAdded responseOutputTextAnnotationAdded:
                if (responseOutputTextAnnotationAdded.Annotation.Type == "container_file_citation")
                {
                    var ann = responseOutputTextAnnotationAdded.Annotation;

                    string? containerId = null;
                    string? fileId = null;
                    string? filename = null;

                    if (ann.AdditionalProperties?.TryGetValue("container_id", out var c) == true)
                        containerId = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString();

                    if (ann.AdditionalProperties?.TryGetValue("file_id", out var f) == true)
                        fileId = f.ValueKind == JsonValueKind.String ? f.GetString() : f.ToString();

                    if (ann.AdditionalProperties?.TryGetValue("filename", out var n) == true)
                        filename = n.ValueKind == JsonValueKind.String ? n.GetString() : n.ToString();

                    yield return CreateSourceUrlEnvelope(
                        responseOutputTextAnnotationAdded.ItemId ?? string.Empty,
                        $"file://{filename}" ?? string.Empty,
                        filename ?? fileId ?? "file",
                        "container_file_citation",
                        containerId,
                        fileId
                    );
                }
                yield break;
            case ResponseFunctionCallArgumentsDelta responseFunctionCallArgumentsDelta:
                yield return CreateToolInputDeltaEnvelope(
                                                      responseFunctionCallArgumentsDelta.ItemId ?? string.Empty,
                                                      responseFunctionCallArgumentsDelta.Delta
                                                  );
                yield break;
            case ResponseCodeInterpreterCallCodeDelta responseCodeInterpreterCallCodeDelta:
                yield return CreateToolInputDeltaEnvelope(
                                                      responseCodeInterpreterCallCodeDelta.ItemId ?? string.Empty,
                                                      responseCodeInterpreterCallCodeDelta.Delta
                                                  );
                yield break;

            case ResponseCustomToolCallInputDelta responseCustomToolCallInputDelta:
                yield return CreateToolInputDeltaEnvelope(
                                                      responseCustomToolCallInputDelta.ItemId ?? string.Empty,
                                                      responseCustomToolCallInputDelta.Delta
                                                  );
                yield break;
            case ResponseCodeInterpreterCallDone responseCodeInterpreterCallDone:
                yield return CreateToolInputDeltaEnvelope(
                                     responseCodeInterpreterCallDone.ItemId ?? string.Empty,
                                     "\"}");

                yield return CreateToolInputEndEnvelope(
                                                      responseCodeInterpreterCallDone.ItemId ?? string.Empty,
                                                      "code_interpreter",
                                                      new
                                                      {
                                                          code = responseCodeInterpreterCallDone.Code
                                                      },
                                                      providerExecuted: true
                                                  );
                yield break;

            case ResponseCustomToolCallInputDone responseCustomToolCallInputDone:

                JsonElement inputCustom;

                object? argsCustom = responseCustomToolCallInputDone.Input;

                try
                {
                    inputCustom = argsCustom switch
                    {
                        JsonElement je => je,

                        string s when !string.IsNullOrWhiteSpace(s)
                            => JsonDocument.Parse(s).RootElement,

                        object o
                            => JsonSerializer.SerializeToElement(o),

                        _ => JsonSerializer.SerializeToElement(new { })
                    };
                }
                catch
                {
                    inputCustom = JsonSerializer.SerializeToElement(new
                    {
                        input = argsCustom
                    });
                }

                yield return CreateToolInputEndEnvelope(
                                                      responseCustomToolCallInputDone.ItemId ?? string.Empty,
                                                      "custom_tool",
                                                     inputCustom,
                                                      providerExecuted: true
                                                  );

                yield return CreateToolOutputEnvelope(
                    responseCustomToolCallInputDone.ItemId ?? string.Empty,
                    new CallToolResult()
                    {
                        Content = [new TextContentBlock() {
                            Text = "No output"
                        }]
                    },
                    providerExecuted: true
            );
                yield break;

            case ResponseMcpCallArgumentsDone responseMcpCallArgumentsDone:

                JsonElement input;

                object? args = responseMcpCallArgumentsDone.Arguments;

                try
                {
                    input = args switch
                    {
                        JsonElement je => je,

                        string s when !string.IsNullOrWhiteSpace(s)
                            => JsonDocument.Parse(s).RootElement,

                        object o
                            => JsonSerializer.SerializeToElement(o),

                        _ => JsonSerializer.SerializeToElement(new { })
                    };
                }
                catch
                {
                    input = JsonSerializer.SerializeToElement(new
                    {
                        arguments = args
                    });
                }

                yield return CreateToolInputEndEnvelope(
                    responseMcpCallArgumentsDone.ItemId ?? string.Empty,
                    "mcp_call",
                    input,
                    providerExecuted: true
                );
                yield break;

            case ResponseMcpCallArgumentsDelta responseMcpCallArgumentsDelta:
                yield return CreateToolInputDeltaEnvelope(
                                         responseMcpCallArgumentsDelta.ItemId ?? string.Empty,
                                         responseMcpCallArgumentsDelta.Delta
                                     );
                yield break;
            case ResponseOutputItemAdded added:
                if (added.Item.Type == "message")
                {
                    yield return CreateTextStartEnvelope(added.Item.Id ?? string.Empty);
                }
                else if (added.Item.Type == "file_search_call")
                {
                    yield return CreateToolInputStartEnvelope(
                        added.Item.Id ?? string.Empty,
                        "file_search",
                        providerExecuted: true
                    );

                    yield break;
                }
                else if (added.Item.Type == "mcp_call")
                {
                    var label = added.Item.AdditionalProperties?.TryGetValue("server_label", out var server_label) == true ? server_label.ToString() : string.Empty;
                    var toolTitle = $"{label} {added.Item.Name}".Trim();

                    yield return CreateToolInputStartEnvelope(
                            added.Item.Id ?? string.Empty,
                             "mcp_call",
                             toolTitle,
                             true
                        );

                    yield break;
                }
                else if (added.Item.Type == "function_call")
                {
                    yield return CreateToolInputStartEnvelope(
                            added.Item.Id ?? string.Empty,
                             added.Item.Name ?? added.Item.Type,
                             added.Item.Name,
                             false
                        );

                    yield break;
                }
                else if (added.Item.Type == "compaction")
                {
                    var encryptedContent = GetAdditionalPropertyValue(added.Item.AdditionalProperties, "encrypted_content");

                    yield return CreateToolInputEndEnvelope(
                        added.Item.Id ?? string.Empty,
                        CompactionToolName,
                        CreateCompactionToolInput(encryptedContent),
                        CompactionToolName,
                        providerExecuted: true,
                        providerMetadata: CreateProviderScopedEncryptedContentMetadata(providerId, encryptedContent));

                    yield break;
                }
                else if (added.Item.Type == "code_interpreter_call")
                {
                    yield return CreateToolInputStartEnvelope(
                             added.Item.Id ?? string.Empty,
                             "code_interpreter",
                             providerExecuted: true
                        );

                    yield return CreateToolInputDeltaEnvelope(
                      added.Item.Id ?? string.Empty,
                      $"{{ \"code\": \""
                 );

                    yield break;
                }
                else if (added.Item.Type == "custom_tool_call")
                {
                    yield return CreateToolInputStartEnvelope(
                             added.Item.Id ?? string.Empty,
                             "custom_tool",
                             added.Item.Name,
                             providerExecuted: true
                        );

                    yield break;
                }

                yield return CreateDataEnvelope(
                       part.Type,
                       JsonSerializer.SerializeToElement(part, part.GetType(), Json));

                yield break;

            case ResponseOutputItemDone done:
                if (done.Item.Type == "message")
                {
                    yield return CreateTextEndEnvelope(done.Item.Id ?? string.Empty);
                }
                else if (done.Item.Type == "reasoning")
                {
                    foreach (var env in CreateReasoningEnvelope(providerId, done.Item.Id ?? string.Empty, done.Item))
                        yield return env;
                }
                else if (done.Item.Type == "compaction")
                {
                    var encryptedContent = GetAdditionalPropertyValue(done.Item.AdditionalProperties, "encrypted_content");

                    yield return CreateToolOutputEnvelope(
                        done.Item.Id ?? string.Empty,
                        CreateCompactionToolOutput(encryptedContent),
                        providerExecuted: true,
                        providerMetadata: CreateProviderScopedEncryptedContentMetadata(providerId, encryptedContent));
                }
                else if (done.Item.Type == "mcp_call")
                {
                    var outputContent = done.Item.AdditionalProperties?.TryGetValue("output", out var output) == true ? output.ToString() : string.Empty;
                    var toolCallResult = new CallToolResult()
                    {
                        Content = [new TextContentBlock() {
                            Text = outputContent
                        }]
                    };

                    yield return CreateToolOutputEnvelope(done.Item.Id ?? string.Empty,
                        toolCallResult);
                }
                else if (done.Item.Type == "function_call")
                {
                    var argumentInput =
                    !string.IsNullOrEmpty(done.Item.Arguments)
                        ? JsonDocument.Parse(done.Item.Arguments).RootElement
                        : JsonSerializer.SerializeToElement(new { });

                    yield return CreateToolInputEndEnvelope(
                            done.Item.Id ?? string.Empty,
                            done.Item.Name ?? done.Item.Type,
                             argumentInput,
                             done.Item.Name,
                              false
                         );
                }
                else if (done.Item.Type == "file_search_call")
                {
                    var fileSearchInput = JsonSerializer.SerializeToElement(
                        new Dictionary<string, object?>
                        {
                            ["queries"] = done.Item.AdditionalProperties?.TryGetValue("queries", out var queries) == true
                                ? queries.Clone()
                                : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonSerializerOptions.Web)
                        },
                        JsonSerializerOptions.Web);

                    yield return CreateToolInputDeltaEnvelope(
                        done.Item.Id ?? string.Empty,
                        fileSearchInput.GetRawText());

                    yield return CreateToolInputEndEnvelope(
                        done.Item.Id ?? string.Empty,
                        "file_search",
                        fileSearchInput,
                        providerExecuted: true
                    );

                    var fileSearchOutput = JsonSerializer.SerializeToElement(
                        new CallToolResult
                        {
                            StructuredContent = JsonSerializer.SerializeToElement(
                                new Dictionary<string, object?>
                                {
                                    ["result"] = done.Item.AdditionalProperties?.TryGetValue("results", out var results) == true
                                        ? results.Clone()
                                        : JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonSerializerOptions.Web)
                                },
                                JsonSerializerOptions.Web)
                        },
                        JsonSerializerOptions.Web);

                    yield return CreateToolOutputEnvelope(
                        done.Item.Id ?? string.Empty,
                        fileSearchOutput,
                        providerExecuted: true
                    );
                }
                else if (done.Item.Type == "search_results")
                {
                    var id = Guid.NewGuid().ToString();
                    JsonElement? searchQueries = done.Item.AdditionalProperties?.TryGetValue("queries", out var queries) == true ? queries.Clone() : null;

                    yield return CreateToolInputStartEnvelope(
                                id,
                                "web_search",
                                 providerExecuted: true
                            );

                    yield return CreateToolInputDeltaEnvelope(
                                id,
                                JsonSerializer.Serialize(new
                                {
                                    queries = searchQueries
                                })
                            );

                    yield return CreateToolInputEndEnvelope(
                                 id,
                                 "web_search",
                                  new
                                  {
                                      queries = searchQueries
                                  },
                                  providerExecuted: true
                             );

                    JsonElement? searchResults = done.Item.AdditionalProperties?.TryGetValue("results", out var results) == true ? results.Clone() : null;

                    yield return CreateToolOutputEnvelope(
                            id,
                            new CallToolResult()
                            {
                                StructuredContent = JsonSerializer.SerializeToElement(new
                                {
                                    results = searchResults
                                })
                            },
                            providerExecuted: true
                        );

                    foreach (var envelope in CreateSourceUrlEnvelopesFromSearchResults(
                                 providerId,
                                 searchResults,
                                 "search_results",
                                 id))
                    {
                        yield return envelope;
                    }

                }
                else if (done.Item.Type == "code_interpreter_call")
                {
                    JsonElement? ciOutput = done.Item.AdditionalProperties?.TryGetValue("outputs", out var output) == true ? output.Clone() : null;
                    string? ciContainer = done.Item.AdditionalProperties?.TryGetValue("container_id", out var container_id) == true ? container_id.ToString() : string.Empty;
                    var toolCallResult = new CallToolResult()
                    {
                        StructuredContent = JsonSerializer.SerializeToElement(new
                        {
                            container_id = ciContainer,
                            outputs = ciOutput,
                        })
                    };

                    yield return CreateToolOutputEnvelope(
                            done.Item.Id ?? string.Empty,
                            toolCallResult,
                            providerExecuted: true
                        );
                }

                else if (done.Item.Type == "image_generation_call")
                {
                    var imgOutput = done.Item.AdditionalProperties?.TryGetValue("result", out var output) == true ? output.ToString() : string.Empty;
                    var imgSize = done.Item.AdditionalProperties?.TryGetValue("size", out var size) == true ? size.ToString() : string.Empty;
                    var revised_prompt = done.Item.AdditionalProperties?.TryGetValue("revised_prompt", out var prompt) == true ? prompt.ToString() : string.Empty;

                    var quality = done.Item.AdditionalProperties?.TryGetValue("quality", out var q) == true ? q.ToString() : string.Empty;
                    var background = done.Item.AdditionalProperties?.TryGetValue("background", out var bg) == true ? bg.ToString() : string.Empty;
                    var action = done.Item.AdditionalProperties?.TryGetValue("action", out var act) == true ? act.ToString() : string.Empty;

                    var imgOutputFormat = done.Item.AdditionalProperties?.TryGetValue("output_format", out var outputFormat) == true ? outputFormat.ToString() : string.Empty;
                    var imageGenResult = new CallToolResult()
                    {
                        StructuredContent = JsonSerializer.SerializeToElement(new
                        {
                            size = imgSize,
                            revised_prompt,
                            quality,
                            background,
                            action
                        }, JsonSerializerOptions.Web),
                        Content = [ImageContentBlock.FromBytes(
                                Convert.FromBase64String(imgOutput),
                                $"image/{imgOutputFormat}"
                            )]
                    };

                    yield return CreateToolOutputEnvelope(
                             done.Item.Id ?? string.Empty,
                             imageGenResult,
                             providerExecuted: true
                        );
                }
                else if (done.Item.Type == "web_search_call")
                {
                    var action = done.Item.AdditionalProperties?
                        .TryGetValue("action", out var a) == true && a is JsonElement je
                            ? je
                            : (JsonElement?)null;

                    if (action is null)
                        yield break;

                    var act = action.Value;

                    act.TryGetProperty("type", out var typeProp);
                    var actionType = typeProp.GetString();

                    JsonElement? queries = null;
                    JsonElement? query = null;
                    JsonElement? sources = null;
                    JsonElement? url = null;
                    JsonElement? pattern = null;

                    if (actionType == "search")
                    {
                        if (act.TryGetProperty("queries", out var q1))
                            queries = q1;

                        if (act.TryGetProperty("query", out var q2))
                            query = q2;

                        if (act.TryGetProperty("sources", out var s))
                            sources = s;
                    }
                    else if (actionType == "open_page")
                    {
                        if (act.TryGetProperty("url", out var u))
                            url = u;

                    }
                    else if (actionType == "find_in_page")
                    {
                        if (act.TryGetProperty("url", out var u))
                            url = u;

                        if (act.TryGetProperty("pattern", out var p))
                            pattern = p;
                    }

                    var inputContent = new Dictionary<string, object?>();

                    if (queries is not null)
                        inputContent["queries"] = queries.Value;

                    if (query is not null)
                        inputContent["query"] = query.Value;

                    if (url is not null)
                        inputContent["url"] = url.Value;

                    if (pattern is not null)
                        inputContent["pattern"] = pattern.Value;

                    yield return CreateToolInputEndEnvelope(
                        done.Item.Id ?? string.Empty,
                        done.Item.Name ?? done.Item.Type,
                        inputContent,
                        $"{done.Item.Name} {actionType}",
                        providerExecuted: true
                    );

                    Dictionary<string, object?>? outputContent = null;

                    if (sources is not null)
                    {
                        outputContent = new Dictionary<string, object?>
                        {
                            ["sources"] = sources.Value
                        };
                    }

                    yield return CreateToolOutputEnvelope(
                        done.Item.Id ?? string.Empty,
                        outputContent ?? [],
                        providerExecuted: true
                    );
                }
                else
                {
                    yield return CreateDataEnvelope(
                            part.Type,
                            JsonSerializer.SerializeToElement(part, part.GetType(), Json));
                }

                yield break;

            case ResponseOutputTextDone done:
                yield return CreateDataEnvelope(done.Type, new Dictionary<string, object?>
                {
                    ["sequence_number"] = done.SequenceNumber,
                    ["item_id"] = done.ItemId,
                    ["content_index"] = done.ContentIndex,
                    ["output_index"] = done.Outputindex,
                    ["text"] = done.Text
                });
                yield break;

            case ResponseError error:
                yield return CreateDataEnvelope(error.Type, new Dictionary<string, object?>
                {
                    ["sequence_number"] = error.SequenceNumber,
                    ["message"] = error.Message,
                    ["param"] = error.Param,
                    ["code"] = error.Code
                });
                yield break;

            case ResponseUnknownEvent unknown
                when string.Equals(unknown.Type, "response.reasoning.search_results", StringComparison.OrdinalIgnoreCase):
                yield return CreateDataEnvelope(
                    unknown.Type,
                    JsonSerializer.SerializeToElement(unknown, unknown.GetType(), Json));

                foreach (var envelope in CreateSourceUrlEnvelopesFromSearchResults(
                             providerId,
                             TryGetUnknownEventProperty(unknown, "results", out var reasoningResults) ? reasoningResults : null,
                             "search_results",
                             $"response.reasoning.search_results:{unknown.SequenceNumber ?? 0}"))
                {
                    yield return envelope;
                }

                yield break;

            default:
                yield return CreateDataEnvelope(
                    part.Type,
                    JsonSerializer.SerializeToElement(part, part.GetType(), Json));
                yield break;
        }
    }

    private static AIEventEnvelope CreateLifecycleEnvelope(string type, int sequenceNumber, ResponseResult response, string providerId)
        => new()
        {
            Type = type,
            Output = new AIOutput { Items = ToUnifiedOutputItems(response, providerId).ToList() },
            Data = new Dictionary<string, object?>
            {
                ["sequence_number"] = sequenceNumber,
                ["response"] = response
            },
            Metadata = new Dictionary<string, object?>
            {
                ["status"] = response.Status,
                ["id"] = response.Id
            }
        };

    private static AIEventEnvelope CreateReasoningStartEnvelope(string providerId, string id, ResponseStreamContentPart responseStreamItem)
            => new()
            {
                Type = "reasoning-start",
                Id = id,
                Data = new AIReasoningStartEventData
                {
                    ProviderMetadata = CreateReasoningProviderMetadata(
                        providerId,
                        encryptedContent: GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content"))
                },
            };

    private static AIEventEnvelope CreateReasoningEndEnvelope(string providerId, string id, ResponseStreamContentPart responseStreamItem)
        => new()
        {
            Type = "reasoning-end",
            Id = id,
            Data = new AIReasoningEndEventData
            {
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    encryptedContent: GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content"),
                    summary: GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "summary"))
            },
        };

    private static AIEventEnvelope CreateSourceUrlEnvelope(string id, string url,
        string title, string type,
        string? containerId = null,
        string? fileId = null,
        string? filename = null,
        Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    => new()
    {
        Type = "source-url",
        Id = id,
        Data = new AISourceUrlEventData
        {
            SourceId = url,
            Url = url,
            Title = title,
            Type = type,
            Filename = filename,
            ContainerId = containerId,
            FileId = fileId,
            ProviderMetadata = providerMetadata
        },
    };

    private static IEnumerable<AIEventEnvelope> CreateSourceUrlEnvelopesFromSearchResults(
        string providerId,
        JsonElement? searchResults,
        string sourceType,
        string idPrefix)
    {
        if (searchResults is not JsonElement results || results.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;

        foreach (var result in results.EnumerateArray())
        {
            index++;

            if (!TryBuildSearchResultSourceEnvelope(
                    providerId,
                    result,
                    sourceType,
                    out var url,
                    out var title,
                    out var providerMetadata))
            {
                continue;
            }

            yield return CreateSourceUrlEnvelope(
                $"{idPrefix}:{index}",
                url!,
                title ?? url!,
                sourceType,
                providerMetadata: providerMetadata);
        }
    }

    private static bool TryBuildSearchResultSourceEnvelope(
        string providerId,
        JsonElement source,
        string sourceType,
        out string? url,
        out string? title,
        out Dictionary<string, Dictionary<string, object>>? providerMetadata)
    {
        url = null;
        title = null;
        providerMetadata = null;

        string? date = null;
        string? lastUpdated = null;
        string? snippet = null;
        string? origin = null;

        if (source.ValueKind == JsonValueKind.Object)
        {
            url = ExtractValue<string>(source, "url")
                ?? ExtractValue<string>(source, "origin_url")
                ?? ExtractValue<string>(source, "image_url");
            title = ExtractValue<string>(source, "title");
            date = ExtractValue<string>(source, "date");
            lastUpdated = ExtractValue<string>(source, "last_updated");
            snippet = ExtractValue<string>(source, "snippet");
            origin = ExtractValue<string>(source, "source");
        }
        else if (source.ValueKind == JsonValueKind.String)
        {
            url = source.GetString();
        }

        if (string.IsNullOrWhiteSpace(url))
            return false;

        providerMetadata = new Dictionary<string, Dictionary<string, object>>
        {
            [providerId] = new Dictionary<string, object>
            {
                ["source_type"] = sourceType,
                ["raw"] = source.Clone()
            }
        };

        if (!string.IsNullOrWhiteSpace(origin))
            providerMetadata[providerId]["origin"] = origin;

        if (!string.IsNullOrWhiteSpace(snippet))
            providerMetadata[providerId]["snippet"] = snippet;

        if (!string.IsNullOrWhiteSpace(date))
            providerMetadata[providerId]["date"] = date;

        if (!string.IsNullOrWhiteSpace(lastUpdated))
            providerMetadata[providerId]["last_updated"] = lastUpdated;

        return true;
    }

    private static bool TryGetUnknownEventProperty(ResponseUnknownEvent unknown, string key, out JsonElement value)
    {
        if (unknown.Data is not null)
        {
            foreach (var property in unknown.Data)
            {
                if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<AIEventEnvelope> CreateReasoningEnvelope(
    string providerId,
    string id,
    ResponseStreamItem responseStreamItem)
    {
        string? reasoning = null;

        if (responseStreamItem.AdditionalProperties?.TryGetValue("summary", out var summaryObj) == true
            && summaryObj is JsonElement summary)
        {
            reasoning = summary.ValueKind switch
            {
                JsonValueKind.Array =>
                    string.Join(
                        "\n\n",
                        summary.EnumerateArray()
                            .Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : x.ToString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                    ),

                JsonValueKind.String => summary.GetString(),

                _ => summary.ToString()
            };
        }

        var summaryVal = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "summary");
        var encrypted = GetAdditionalPropertyValue(responseStreamItem.AdditionalProperties, "encrypted_content");
        yield return new AIEventEnvelope
        {
            Type = "reasoning-start",
            Id = id,
            Data = new AIReasoningStartEventData
            {
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    encryptedContent: encrypted)
            }
        };

        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            yield return new AIEventEnvelope
            {
                Type = "reasoning-delta",
                Id = id,
                Data = new AIReasoningDeltaEventData
                {
                    Delta = reasoning
                }
            };
        }

        yield return new AIEventEnvelope
        {
            Type = "reasoning-end",
            Id = id,
            Data = new AIReasoningEndEventData
            {
                ProviderMetadata = CreateReasoningProviderMetadata(
                    providerId,
                    encryptedContent: encrypted,
                    summary: summaryVal)
            }
        };
    }

    private static object? GetAdditionalPropertyValue(Dictionary<string, JsonElement>? properties, string key)
    {
        if (properties?.TryGetValue(key, out var value) != true)
            return null;

        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.Clone();
    }

    private static string? TryGetAnnotationString(ResponseStreamAnnotation annotation, string key)
    {
        if (annotation.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ToString();
    }

    private static int? TryGetAnnotationInt(ResponseStreamAnnotation annotation, string key)
    {
        if (annotation.AdditionalProperties?.TryGetValue(key, out var value) != true)
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return null;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateProviderMetadata(
        string providerId,
        Dictionary<string, object?> providerMetadata)
    {
        var values = providerMetadata
            .Where(static entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!);

        return values.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = values
            };
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateReasoningProviderMetadata(
        string providerId,
        string? signature = null,
        object? encryptedContent = null,
        object? summary = null)
    {
        var providerMetadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(signature))
            providerMetadata["signature"] = signature;

        if (HasMeaningfulReasoningValue(encryptedContent))
            providerMetadata["encrypted_content"] = encryptedContent!;

        if (HasMeaningfulReasoningValue(summary))
            providerMetadata["summary"] = summary!;

        return providerMetadata.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            };
    }

    private static bool HasMeaningfulReasoningValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement json => json.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined,
            _ => true
        };

    private static AIEventEnvelope CreateReasoningDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "reasoning-delta",
                Id = id,
                Data = new AIReasoningDeltaEventData
                {
                    Delta = delta
                }
            };

    private static AIEventEnvelope CreateToolInputStartEnvelope(string id,
        string toolname,
        string? title = null,
        bool? providerExecuted = false)
           => new()
           {
               Type = "tool-input-start",
               Id = id,
               Data = new AIToolInputStartEventData
               {
                   ProviderExecuted = providerExecuted,
                   ToolName = toolname,
                   Title = title
               },
           };

    private static AIEventEnvelope CreateToolInputDeltaEnvelope(string id,
            string delta)
               => new()
               {
                   Type = "tool-input-delta",
                   Id = id,
                   Data = new AIToolInputDeltaEventData
                   {
                       InputTextDelta = delta
                   }
               };

    private static AIEventEnvelope CreateToolInputEndEnvelope(string id,
        string toolname,
        object input,
        string? title = null,
        bool? providerExecuted = false,
        Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    => new()
    {
        Type = "tool-input-available",
        Id = id,
        Data = new AIToolInputAvailableEventData
        {
            ProviderExecuted = providerExecuted,
            ToolName = toolname,
            Input = input,
            Title = title,
            ProviderMetadata = providerMetadata
        },
    };

    private static AIEventEnvelope CreateToolOutputEnvelope(string id,
           object output,
           bool? preliminary = null,
           bool? dynamic = null,
           bool? providerExecuted = false,
           Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
       => new()
       {
           Type = "tool-output-available",
           Id = id,
           Data = new AIToolOutputAvailableEventData
           {
               ProviderExecuted = providerExecuted,
               Preliminary = preliminary,
               Dynamic = dynamic,
               Output = output,
               ProviderMetadata = providerMetadata,
            },
        };

    private static AIEventEnvelope CreateTextStartEnvelope(string id)
        => new()
        {
            Type = "text-start",
            Id = id,
            Data = new AITextStartEventData()
        };

    private static AIEventEnvelope CreateTextEndEnvelope(string id)
        => new()
        {
            Type = "text-end",
            Id = id,
            Data = new AITextEndEventData()
        };

    private static AIEventEnvelope CreateTextDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "text-delta",
                Id = id,
                Data = new AITextDeltaEventData
                {
                    Delta = delta
                }
            };

    private static AIEventEnvelope CreateFinishEnvelope(string id, int sequenceNumber, ResponseResult response)
    {
        var usage = response.Usage is JsonElement je ? je : default;

        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;

        if (usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("input_tokens", out var i))
                inputTokens = i.GetInt32();

            if (usage.TryGetProperty("output_tokens", out var o))
                outputTokens = o.GetInt32();

            if (usage.TryGetProperty("total_tokens", out var t))
                totalTokens = t.GetInt32();
        }

        return new()
        {
            Type = "finish",
            Id = id,
            Data = new AIFinishEventData
            {
                SequenceNumber = sequenceNumber,
                Response = response,
                Model = response.Model,
                CompletedAt = response.CompletedAt,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                FinishReason = response.Status == "failed" ? "error"
                    : response.Output.Any(a => a is ResponseFunctionCallItem) ? "tool-calls"
                    : response.Status == "completed" ? "stop"
                    : "other"
            },
            Metadata = response.Metadata
        };
    }

    private static AIEventEnvelope CreateDataEnvelope(string type, object? data)
        => new()
        {
            Type = $"data-responses.{type}",
            Data = new AIDataEventData
            {
                Data = data ?? new { }
            }
        };

    private static ResponseResult GetResponseResult(Dictionary<string, object?> data, AIEventEnvelope envelope)
    {
        if (data.TryGetValue("response", out var responseObj) && responseObj is not null)
        {
            try
            {
                return responseObj is ResponseResult existing
                    ? existing
                    : JsonSerializer.Deserialize<ResponseResult>(JsonSerializer.Serialize(responseObj, Json), Json)
                      ?? new ResponseResult { Id = Guid.NewGuid().ToString("N"), Model = "unknown" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseResult
        {
            Id = Guid.NewGuid().ToString("N"),
            Object = "response",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = ExtractValue<string>(envelope.Metadata, "status"),
            Model = ExtractValue<string>(envelope.Metadata, "model") ?? "unknown",
            Output = []
        };
    }

    private static ResponseStreamItem GetResponseStreamItem(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("item", out var itemObj) && itemObj is not null)
        {
            try
            {
                return itemObj is ResponseStreamItem item
                    ? item
                    : JsonSerializer.Deserialize<ResponseStreamItem>(JsonSerializer.Serialize(itemObj, Json), Json)
                      ?? new ResponseStreamItem { Type = "message" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamItem { Type = "message" };
    }

    private static ResponseStreamContentPart GetResponseStreamContentPart(Dictionary<string, object?> data, string key)
    {
        if (data.TryGetValue(key, out var partObj) && partObj is not null)
        {
            try
            {
                return partObj is ResponseStreamContentPart part
                    ? part
                    : JsonSerializer.Deserialize<ResponseStreamContentPart>(JsonSerializer.Serialize(partObj, Json), Json)
                      ?? new ResponseStreamContentPart { Type = "output_text" };
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamContentPart { Type = "output_text" };
    }

    private static ResponseStreamAnnotation GetResponseStreamAnnotation(Dictionary<string, object?> data)
    {
        if (data.TryGetValue("annotation", out var annotationObj) && annotationObj is not null)
        {
            try
            {
                return annotationObj is ResponseStreamAnnotation annotation
                    ? annotation
                    : JsonSerializer.Deserialize<ResponseStreamAnnotation>(JsonSerializer.Serialize(annotationObj, Json), Json)
                      ?? new ResponseStreamAnnotation();
            }
            catch
            {
                // ignored
            }
        }

        return new ResponseStreamAnnotation();
    }

    private static Dictionary<string, JsonElement>? ToJsonElementMap(object? value)
    {
        if (value is null)
            return null;

        try
        {
            if (value is Dictionary<string, JsonElement> already)
                return already;

            if (value is JsonElement json && json.ValueKind == JsonValueKind.Object)
            {
                return json.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value);
            }

            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return null;
        }
    }
}
