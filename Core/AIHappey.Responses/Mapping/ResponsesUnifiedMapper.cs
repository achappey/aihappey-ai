using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Responses.Mapping;

public static class ResponsesUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static AIRequest ToUnifiedRequest(this ResponseRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Instructions = request.Instructions,
            Input = request.Input is null ? null : ToUnifiedInput(request.Input),
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    public static ResponseRequest ToResponseRequest(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();

        return new ResponseRequest
        {
            Model = request.Model,
            Instructions = request.Instructions,
            Input = request.Input is null ? null : ToResponsesInput(request.Input),
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ParallelToolCalls,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Select(ToResponsesTool).ToList(),
            Metadata = request.Metadata,
            Store = ExtractValue<bool?>(metadata, "responses.store"),
            ServiceTier = ExtractValue<string>(metadata, "responses.service_tier"),
            //Include = ExtractObject<List<string>>(metadata, "responses.include"),
            Text = metadata.TryGetValue("responses.text", out var text) ? text : null,
            TopLogprobs = ExtractValue<int?>(metadata, "responses.top_logprobs"),
            Truncation = ParseTruncation(metadata, "responses.truncation"),
            Reasoning = ExtractObject<Reasoning>(metadata, "responses.reasoning"),
            ContextManagement = ExtractObject<JsonElement[]>(metadata, "responses.context_management")
        };
    }

    public static AIResponse ToUnifiedResponse(this ResponseResult response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model,
            Status = response.Status,
            Usage = response.Usage,
            Output = outputItems.Count > 0 ? new AIOutput { Items = outputItems } : null,
            Metadata = BuildUnifiedResponseMetadata(response),
            //  Events = null
        };
    }

    public static ResponseResult ToResponseResult(AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? new Dictionary<string, object?>();

        return new ResponseResult
        {
            Id = ExtractValue<string>(metadata, "responses.id") ?? Guid.NewGuid().ToString("N"),
            Object = ExtractValue<string>(metadata, "responses.object") ?? "response",
            CreatedAt = ExtractValue<long?>(metadata, "responses.created_at") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            CompletedAt = ExtractValue<long?>(metadata, "responses.completed_at"),
            Status = response.Status,
            ParallelToolCalls = ExtractValue<bool?>(metadata, "responses.parallel_tool_calls"),
            Model = response.Model ?? "unknown",
            Temperature = ExtractValue<float?>(metadata, "responses.temperature"),
            Usage = response.Usage,
            Output = ToResponseOutputObjects(response.Output).ToList(),
            Text = ExtractObject<object>(metadata, "responses.text"),
            ToolChoice = ExtractObject<object>(metadata, "responses.tool_choice"),
            Tools = ExtractObject<List<object>>(metadata, "responses.tools") ?? [],
            Reasoning = ExtractObject<object>(metadata, "responses.reasoning"),
            Store = ExtractValue<bool?>(metadata, "responses.store"),
            MaxOutputTokens = ExtractValue<int?>(metadata, "responses.max_output_tokens"),
            ServiceTier = ExtractValue<string>(metadata, "responses.service_tier"),
            Error = ExtractObject<ResponseResultError>(metadata, "responses.error"),
            Metadata = ExtractObject<Dictionary<string, object?>>(metadata, "responses.metadata")
        };
    }

    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvent(
        this ResponseStreamPart part,
        string providerId)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        foreach (var envelope in ToUnifiedEnvelope(part))
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

    public static ResponseStreamPart ToResponseStreamPart(AIStreamEvent streamEvent)
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

    private static AIInput ToUnifiedInput(ResponseInput input)
    {
        if (input.IsText)
            return new AIInput { Text = input.Text };

        var items = input.Items?.Select(ToUnifiedInputItem).ToList();
        return new AIInput { Items = items };
    }

    private static ResponseInput ToResponsesInput(AIInput input)
    {
        if (!string.IsNullOrWhiteSpace(input.Text))
            return new ResponseInput(input.Text);

        var items = input.Items?.Select(ToResponsesInputItem).ToList() ?? [];
        return new ResponseInput(items);
    }

    private static AIInputItem ToUnifiedInputItem(ResponseInputItem item)
    {
        switch (item)
        {
            case ResponseInputMessage message:
                return new AIInputItem
                {
                    Type = "message",
                    Role = message.Role.ToString().ToLowerInvariant(),
                    Content = ToUnifiedContentParts(message.Content).ToList(),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = message.Id,
                        ["status"] = message.Status,
                        ["phase"] = message.Phase
                    }
                };

            case ResponseFunctionCallItem call:
                return new AIInputItem
                {
                    Type = "function_call",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = call.Id,
                        ["call_id"] = call.CallId,
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments,
                        ["status"] = call.Status
                    }
                };

            case ResponseFunctionCallOutputItem output:
                return new AIInputItem
                {
                    Type = "function_call_output",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = output.Id,
                        ["call_id"] = output.CallId,
                        ["output"] = output.Output,
                        ["status"] = output.Status
                    }
                };

            case ResponseReasoningItem reasoning:
                return new AIInputItem
                {
                    Type = "reasoning",
                    Content = reasoning.Summary
                        .Select(a => (AIContentPart)new AITextContentPart { Type = "text", Text = a.Text, Metadata = new Dictionary<string, object?> { ["type"] = a.Type } })
                        .ToList(),
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = reasoning.Id,
                        ["encrypted_content"] = reasoning.EncryptedContent
                    }
                };

            case ResponseImageGenerationCallItem imageGen:
                return new AIInputItem
                {
                    Type = "image_generation_call",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["id"] = imageGen.Id,
                        ["result"] = imageGen.Result,
                        ["status"] = imageGen.Status
                    }
                };

            case ResponseItemReference reference:
                return new AIInputItem
                {
                    Type = "item_reference",
                    Metadata = new Dictionary<string, object?> { ["id"] = reference.Id }
                };

            default:
                return new AIInputItem { Type = item.Type ?? "item", Metadata = new Dictionary<string, object?> { ["raw"] = item } };
        }
    }

    private static ResponseInputItem ToResponsesInputItem(AIInputItem item)
    {
        var kind = item.Type?.Trim().ToLowerInvariant();
        var metadata = item.Metadata ?? new Dictionary<string, object?>();

        return kind switch
        {
            "message" => new ResponseInputMessage
            {
                Role = ParseRole(item.Role),
                Content = new ResponseMessageContent(ToResponsesContentParts(item.Content, item.Role).ToList()),
                Id = ExtractValue<string>(metadata, "id"),
                Status = ExtractValue<string>(metadata, "status"),
                Phase = ExtractValue<string>(metadata, "phase")
            },
            "function_call" => new ResponseFunctionCallItem
            {
                Id = ExtractValue<string>(metadata, "id"),
                CallId = ExtractValue<string>(metadata, "call_id") ?? string.Empty,
                Name = ExtractValue<string>(metadata, "name") ?? "unknown",
                Arguments = ExtractValue<string>(metadata, "arguments") ?? "{}",
                Status = ExtractValue<string>(metadata, "status")
            },
            "function_call_output" => new ResponseFunctionCallOutputItem
            {
                Id = ExtractValue<string>(metadata, "id"),
                CallId = ExtractValue<string>(metadata, "call_id") ?? string.Empty,
                Output = ExtractValue<string>(metadata, "output") ?? "{}",
                Status = ExtractValue<string>(metadata, "status")
            },
            "reasoning" => new ResponseReasoningItem
            {
                Id = ExtractValue<string>(metadata, "id"),
                EncryptedContent = ExtractValue<string>(metadata, "encrypted_content"),
                Summary = (item.Content ?? [])
                    .OfType<AITextContentPart>()
                    .Select(a => new ResponseReasoningSummaryTextPart { Text = a.Text })
                    .ToList()
            },
            "image_generation_call" => new ResponseImageGenerationCallItem
            {
                Id = ExtractValue<string>(metadata, "id"),
                Result = ExtractValue<string>(metadata, "result") ?? string.Empty,
                Status = ExtractValue<string>(metadata, "status")
            },
            "item_reference" => new ResponseItemReference
            {
                Id = ExtractValue<string>(metadata, "id") ?? string.Empty
            },
            _ => new ResponseInputMessage
            {
                Role = ParseRole(item.Role),
                Content = new ResponseMessageContent(ToResponsesContentParts(item.Content, item.Role).ToList())
            }
        };
    }

    private static IEnumerable<AIContentPart> ToUnifiedContentParts(ResponseMessageContent content)
    {
        if (content.IsText && !string.IsNullOrWhiteSpace(content.Text))
        {
            yield return new AITextContentPart { Type = "text", Text = content.Text! };
            yield break;
        }

        if (!content.IsParts || content.Parts is null)
            yield break;

        foreach (var part in content.Parts)
        {
            switch (part)
            {
                case InputTextPart inputText:
                    yield return new AITextContentPart { Type = "text", Text = inputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "input_text" } };
                    break;
                case OutputTextPart outputText:
                    yield return new AITextContentPart { Type = "text", Text = outputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "output_text", ["responses.annotations"] = outputText.Annotations } };
                    break;
                case InputImagePart image:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(image.ImageUrl),
                        Filename = image.FileId,
                        Data = image.ImageUrl,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_image",
                            ["responses.detail"] = image.Detail,
                            ["responses.file_id"] = image.FileId
                        }
                    };
                    break;
                case InputFilePart file:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(file.FileData ?? file.FileUrl),
                        Filename = file.Filename,
                        Data = file.FileData ?? file.FileUrl ?? file.FileId,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_file",
                            ["responses.file_id"] = file.FileId,
                            ["responses.file_url"] = file.FileUrl
                        }
                    };
                    break;
            }
        }
    }

    private static IEnumerable<ResponseContentPart> ToResponsesContentParts(IEnumerable<AIContentPart>? parts, string? role)
    {
        foreach (var part in parts ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    {
                        if (role == "assistant")
                        {
                            yield return new OutputTextPart
                            {
                                Text = text.Text,
                                Annotations = ExtractObject<object[]>(text.Metadata, "responses.annotations") ?? []
                            };
                        }
                        else
                        {
                            yield return new InputTextPart(text.Text);
                        }

                        break;
                    }

                case AIFileContentPart file:
                    {
                        if (file.MediaType?.StartsWith("image/") == true)
                        {
                            yield return new InputImagePart
                            {
                                ImageUrl = file.Data?.ToString(),
                            };
                        }
                        else
                        {
                            var dataText = file.Data?.ToString();
                            yield return new InputFilePart
                            {
                                Filename = file.Filename,
                                FileId = ExtractValue<string>(file.Metadata, "responses.file_id"),
                                FileData = dataText is not null && dataText.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? dataText : null,
                                FileUrl = dataText is not null && dataText.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? dataText : ExtractValue<string>(file.Metadata, "responses.file_url")
                            };
                        }

                        break;
                    }
            }
        }
    }

    private static AIToolDefinition ToUnifiedTool(ResponseToolDefinition tool)
        => new()
        {
            Name = tool.Extra is not null && tool.Extra.TryGetValue("name", out var n) ? n.GetString() ?? tool.Type : tool.Type,
            Description = tool.Extra is not null && tool.Extra.TryGetValue("description", out var d) ? d.GetString() : null,
            InputSchema = tool.Extra is not null && tool.Extra.TryGetValue("parameters", out var p) ? p : null,
            Metadata = new Dictionary<string, object?>
            {
                ["responses.type"] = tool.Type,
                ["responses.extra"] = tool.Extra
            }
        };

    private static ResponseToolDefinition ToResponsesTool(AIToolDefinition tool)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(tool.Name, Json)
        };

        if (!string.IsNullOrWhiteSpace(tool.Description))
            extra["description"] = JsonSerializer.SerializeToElement(tool.Description, Json);

        if (tool.InputSchema is not null)
            extra["parameters"] = JsonSerializer.SerializeToElement(tool.InputSchema, Json);

        return new ResponseToolDefinition
        {
            Type = ExtractValue<string>(tool.Metadata, "responses.type") ?? "function",
            Extra = extra
        };
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(ResponseResult response)
    {
        foreach (var item in response.Output ?? [])
        {
            if (item is null)
                continue;

            var map = ToJsonMap(item);
            var role = GetValue<string>(map, "role") ?? "assistant";
            var type = GetValue<string>(map, "type") ?? "message";

            var content = new List<AIContentPart>();

            if (map.TryGetValue("content", out var contentObj))
            {
                if (contentObj is JsonElement contentJson && contentJson.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in contentJson.EnumerateArray())
                    {
                        var partType = part.TryGetProperty("type", out var t) ? t.GetString() : null;
                        if (partType is "output_text" or "input_text")
                        {
                            content.Add(new AITextContentPart
                            {
                                Type = "text",
                                Text = part.TryGetProperty("text", out var textProp) ? textProp.GetString() ?? string.Empty : string.Empty,
                                Metadata = new Dictionary<string, object?> { ["responses.type"] = partType }
                            });
                        }
                        else
                        {
                            content.Add(new AITextContentPart
                            {
                                Type = "text",
                                Text = part.GetRawText(),
                                Metadata = new Dictionary<string, object?> { ["responses.raw_content_part"] = true }
                            });
                        }
                    }
                }
            }

            if (content.Count == 0 && map.TryGetValue("text", out var textObj))
            {
                var text = textObj is JsonElement e ? e.ToString() : textObj?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    content.Add(new AITextContentPart { Type = "text", Text = text! });
            }

            yield return new AIOutputItem
            {
                Type = type,
                Role = role,
                Content = content.Count > 0 ? content : null,
                Metadata = new Dictionary<string, object?>
                {
                    ["responses.raw_output"] = item
                }
            };
        }

        if (response.Output?.Any() != true && response.Text is not null)
        {
            yield return new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = [new AITextContentPart { Type = "text", Text = response.Text.ToString() ?? string.Empty }]
            };
        }
    }

    private static IEnumerable<object> ToResponseOutputObjects(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            var content = new List<object>();
            foreach (var part in item.Content ?? [])
            {
                switch (part)
                {
                    case AITextContentPart text:
                        content.Add(new OutputTextPart(text.Text));
                        break;
                    case AIFileContentPart file:
                        content.Add(new InputFilePart
                        {
                            Filename = file.Filename,
                            FileData = file.Data?.ToString()
                        });
                        break;
                }
            }

            yield return new
            {
                type = item.Type,
                role = item.Role,
                content
            };
        }
    }

    private static IEnumerable<AIEventEnvelope> ToUnifiedEnvelope(ResponseStreamPart part)
    {
        switch (part)
        {
            case ResponseCreated created:
                yield return CreateLifecycleEnvelope(created.Type, created.SequenceNumber, created.Response);
                yield break;

            case ResponseInProgress inProgress:
                yield return CreateLifecycleEnvelope(inProgress.Type, inProgress.SequenceNumber, inProgress.Response);
                yield break;

            case ResponseCompleted completed:
                yield return CreateLifecycleEnvelope(completed.Type, completed.SequenceNumber, completed.Response);
                yield return CreateFinishEnvelope(completed.Type,
                    completed.SequenceNumber, completed.Response);
                yield break;

            case ResponseFailed failed:
                yield return CreateLifecycleEnvelope(failed.Type, failed.SequenceNumber, failed.Response);
                yield break;

            case ResponseReasoningSummaryPartAdded added:
                yield return CreateReasoningStartEnvelope(
                    added.ItemId ?? string.Empty,
                    added.Part);
                yield break;

            case ResponseReasoningSummaryPartDone done:
                yield return CreateReasoningEndEnvelope(
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
                        $"file://{filename}" ?? string.Empty,                     // url (best we have)
                        filename ?? fileId ?? "file",               // title
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
                    foreach (var env in CreateReasoningEnvelope(done.Item.Id ?? string.Empty, done.Item))
                        yield return env;
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

                    // build input
                    var inputContent = new Dictionary<string, object?>();

                    if (queries is not null)
                        inputContent["queries"] = queries.Value;

                    if (query is not null)
                        inputContent["query"] = query.Value;

                    if (url is not null)
                        inputContent["url"] = url.Value;

                    if (pattern is not null)
                        inputContent["pattern"] = pattern.Value;

                    // 1. tool-input-end
                    yield return CreateToolInputEndEnvelope(
                        done.Item.Id ?? string.Empty,
                        done.Item.Name ?? done.Item.Type,
                        inputContent,
                        $"{done.Item.Name} {actionType}",
                        providerExecuted: true
                    );

                    // build output
                    Dictionary<string, object?>? outputContent = null;

                    if (sources is not null)
                    {
                        outputContent = new Dictionary<string, object?>
                        {
                            ["sources"] = sources.Value
                        };
                    }

                    // 2. tool-output-available
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

            default:
                yield return CreateDataEnvelope(
                    part.Type,
                    JsonSerializer.SerializeToElement(part, part.GetType(), Json));
                yield break;
        }
    }

    private static AIEventEnvelope CreateLifecycleEnvelope(string type, int sequenceNumber, ResponseResult response)
        => new()
        {
            Type = type,
            Output = new AIOutput { Items = ToUnifiedOutputItems(response).ToList() },
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

    private static AIEventEnvelope CreateReasoningStartEnvelope(string id, ResponseStreamContentPart responseStreamItem)
            => new()
            {
                Type = "reasoning-start",
                Id = id,
                Data = new Dictionary<string, object?>
                {
                    ["encrypted_content"] =
                        responseStreamItem.AdditionalProperties?.TryGetValue("encrypted_content", out var encrypted_content) == true ? encrypted_content.ToString() : string.Empty
                },
            };


    private static AIEventEnvelope CreateReasoningEndEnvelope(string id, ResponseStreamContentPart responseStreamItem)
        => new()
        {
            Type = "reasoning-end",
            Id = id,
            Data = new Dictionary<string, object?>
            {
                ["encrypted_content"] =
                    responseStreamItem.AdditionalProperties?.TryGetValue("encrypted_content", out var encrypted_content) == true ? encrypted_content.ToString() : string.Empty
            },
        };

    private static AIEventEnvelope CreateSourceUrlEnvelope(string id, string url,
        string title, string type,
        string? containerId = null,
        string? fileId = null)
    => new()
    {
        Type = "source-url",
        Id = id,
        Data = new Dictionary<string, object?>
        {
            ["url"] = url,
            ["title"] = title,
            ["type"] = type,
            ["container_id"] = containerId,
            ["file_id"] = fileId
        },
    };

    private static IEnumerable<AIEventEnvelope> CreateReasoningEnvelope(
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

        JsonElement? summaryVal =
      responseStreamItem.AdditionalProperties?.TryGetValue("summary", out var s) == true ? s : null;

        JsonElement? encrypted =
            responseStreamItem.AdditionalProperties?.TryGetValue("encrypted_content", out var e) == true ? e : null;
        // start
        yield return new AIEventEnvelope
        {
            Type = "reasoning-start",
            Id = id
        };

        // delta (only if exists)
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            yield return new AIEventEnvelope
            {
                Type = "reasoning-delta",
                Id = id,
                Data = reasoning
            };
        }

        // end
        yield return new AIEventEnvelope
        {
            Type = "reasoning-end",
            Id = id,
            Data = new Dictionary<string, object?>
            {
                ["summary"] = summaryVal,
                ["encrypted_content"] = encrypted
            }
        };
    }

    private static AIEventEnvelope CreateReasoningDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "reasoning-delta",
                Id = id,
                Data = delta
            };

    private static AIEventEnvelope CreateToolInputStartEnvelope(string id,
        string toolname,
        string? title = null,
        bool? providerExecuted = false)
           => new()
           {
               Type = "tool-input-start",
               Id = id,
               Data = new Dictionary<string, object?>
               {
                   ["providerExecuted"] = providerExecuted,
                   ["toolName"] = toolname,
                   ["title"] = title
               },
           };

    private static AIEventEnvelope CreateToolInputDeltaEnvelope(string id,
            string delta)
               => new()
               {
                   Type = "tool-input-delta",
                   Id = id,
                   Data = delta
               };

    private static AIEventEnvelope CreateToolInputEndEnvelope(string id,
        string toolname,
        object input,
        string? title = null,
        bool? providerExecuted = false)
    => new()
    {
        Type = "tool-input-available",
        Id = id,
        Data = new Dictionary<string, object?>
        {
            ["providerExecuted"] = providerExecuted,
            ["toolName"] = toolname,
            ["input"] = input,
            ["title"] = title
        },
    };


    private static AIEventEnvelope CreateToolOutputEnvelope(string id,
           object output,
           bool? preliminary = null,
           bool? dynamic = null,
           bool? providerExecuted = false)
       => new()
       {
           Type = "tool-output-available",
           Id = id,
           Data = new Dictionary<string, object?>
           {
               ["providerExecuted"] = providerExecuted,
               ["preliminary"] = preliminary,
               ["dynamic"] = dynamic,
               ["output"] = output,
           },
       };

    private static AIEventEnvelope CreateTextStartEnvelope(string id)
        => new()
        {
            Type = "text-start",
            Id = id,
        };

    private static AIEventEnvelope CreateTextEndEnvelope(string id)
        => new()
        {
            Type = "text-end",
            Id = id,
        };

    private static AIEventEnvelope CreateTextDeltaEnvelope(string id, string delta)
            => new()
            {
                Type = "text-delta",
                Id = id,
                Data = delta
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
            Data = new Dictionary<string, object?>
            {
                ["sequence_number"] = sequenceNumber,
                ["response"] = response,
                ["model"] = response.Model,
                ["completed_at"] = response.CompletedAt,

                ["inputTokens"] = inputTokens,
                ["outputTokens"] = outputTokens,
                ["totalTokens"] = totalTokens,

                ["finishReason"] = response.Status == "failed" ? "error"
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
            Type = type,
            Data = data
        };

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(ResponseRequest request)
        => new()
        {
            ["responses.metadata"] = request.Metadata,
            ["responses.store"] = request.Store,
            ["responses.service_tier"] = request.ServiceTier,
            ["responses.include"] = request.Include,
            ["responses.text"] = request.Text,
            ["responses.top_logprobs"] = request.TopLogprobs,
            ["responses.truncation"] = request.Truncation,
            ["responses.reasoning"] = request.Reasoning,
            ["responses.context_management"] = request.ContextManagement
        };

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(ResponseResult response)
        => new()
        {
            ["responses.id"] = response.Id,
            ["responses.object"] = response.Object,
            ["responses.created_at"] = response.CreatedAt,
            ["responses.completed_at"] = response.CompletedAt,
            ["responses.parallel_tool_calls"] = response.ParallelToolCalls,
            ["responses.temperature"] = response.Temperature,
            ["responses.text"] = response.Text,
            ["responses.tool_choice"] = response.ToolChoice,
            ["responses.tools"] = response.Tools?.ToList(),
            ["responses.reasoning"] = response.Reasoning,
            ["responses.store"] = response.Store,
            ["responses.max_output_tokens"] = response.MaxOutputTokens,
            ["responses.service_tier"] = response.ServiceTier,
            ["responses.error"] = response.Error,
            ["metadata"] = response.Metadata
        };

    private static ResponseRole ParseRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => ResponseRole.Assistant,
            "system" => ResponseRole.System,
            "developer" => ResponseRole.Developer,
            _ => ResponseRole.User
        };

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf(';');
            if (end > 5)
                return value[5..end];
        }

        return null;
    }

    private static T? ExtractObject<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static T? ExtractValue<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return default;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json);
        }
        catch
        {
            return default;
        }
    }

    private static TEnum? ExtractEnum<TEnum>(Dictionary<string, object?>? metadata, string key)
        where TEnum : struct
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return default;

        if (Enum.TryParse<TEnum>(raw, true, out var parsed))
            return parsed;

        return default;
    }

    private static Dictionary<string, object?> ToJsonMap(object? value)
    {
        if (value is null)
            return new Dictionary<string, object?>();

        if (value is Dictionary<string, object?> dict)
            return dict;

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value, Json), Json)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static T GetValue<T>(Dictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return default!;

        if (value is T cast)
            return cast;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), Json)!;

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
        }
        catch
        {
            return default!;
        }
    }

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

    private static TruncationStrategy? ParseTruncation(Dictionary<string, object?>? metadata, string key)
    {
        var raw = ExtractValue<string>(metadata, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => TruncationStrategy.Auto,
            "disabled" => TruncationStrategy.Disabled,
            _ => null
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

