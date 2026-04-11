using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static IEnumerable<AIContentPart> ToUnifiedContentParts(IEnumerable<InteractionContent>? content, string providerId)
    {
        foreach (var part in content ?? [])
        {
            switch (part)
            {
                case InteractionTextContent text:
                    yield return new AITextContentPart
                    {
                        Type = "text",
                        Text = text.Text ?? string.Empty,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["interactions.content.type"] = "text",
                            ["interactions.annotations"] = text.Annotations,
                            ["interactions.raw"] = JsonSerializer.SerializeToElement(part, Json)
                        }
                    };
                    break;

                case InteractionImageContent image:
                    yield return CreateFilePart("image", image.MimeType, image.Data ?? image.Uri, image.Uri, image.Resolution, part);
                    break;

                case InteractionAudioContent audio:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = audio.MimeType,
                        Data = audio.Data ?? audio.Uri,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["interactions.content.type"] = "audio",
                            ["interactions.mime_type"] = audio.MimeType,
                            ["interactions.uri"] = audio.Uri,
                            ["interactions.rate"] = audio.Rate,
                            ["interactions.channels"] = audio.Channels,
                            ["interactions.raw"] = JsonSerializer.SerializeToElement(part, Json)
                        }
                    };
                    break;

                case InteractionDocumentContent document:
                    yield return CreateFilePart("document", document.MimeType, document.Data ?? document.Uri, document.Uri, null, part);
                    break;

                case InteractionVideoContent video:
                    yield return CreateFilePart("video", video.MimeType, video.Data ?? video.Uri, video.Uri, video.Resolution, part);
                    break;

                case InteractionThoughtContent thought:
                    yield return new AIReasoningContentPart
                    {
                        Type = "reasoning",
                        Text = FlattenContentText(thought.Summary),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["interactions.content.type"] = "thought",
                            ["interactions.signature"] = thought.Signature,
                            ["interactions.summary"] = thought.Summary,
                            ["interactions.raw"] = JsonSerializer.SerializeToElement(part, Json)
                        }
                    };
                    break;

                case InteractionFunctionCallContent call:
                    yield return CreateToolPart("function_call", call.Id, call.Name, call.Arguments, null, false, call.Signature, null, part);
                    break;

                case InteractionCodeExecutionCallContent codeCall:
                    yield return CreateToolPart("code_execution_call", codeCall.Id, "code_execution", codeCall.Arguments, null, true, codeCall.Signature, null, part);
                    break;

                case InteractionUrlContextCallContent urlCall:
                    yield return CreateToolPart("url_context_call", urlCall.Id, "url_context", urlCall.Arguments, null, true, urlCall.Signature, null, part);
                    break;

                case InteractionMcpServerToolCallContent mcpCall:
                    yield return CreateToolPart("mcp_server_tool_call", mcpCall.Id, mcpCall.Name ?? "mcp_server", mcpCall.Arguments, null, true, mcpCall.Signature,
                        new Dictionary<string, object?> { ["interactions.server_name"] = mcpCall.ServerName }, part);
                    break;

                case InteractionGoogleSearchCallContent googleSearchCall:
                    yield return CreateToolPart("google_search_call", googleSearchCall.Id, "google_search", googleSearchCall.Arguments, null, true, googleSearchCall.Signature,
                        new Dictionary<string, object?> { ["interactions.search_type"] = googleSearchCall.SearchType }, part);
                    break;

                case InteractionFileSearchCallContent fileSearchCall:
                    yield return CreateToolPart("file_search_call", fileSearchCall.Id, "file_search", new { }, null, true, fileSearchCall.Signature, null, part);
                    break;

                case InteractionGoogleMapsCallContent mapsCall:
                    yield return CreateToolPart("google_maps_call", mapsCall.Id, "google_maps", (object?)mapsCall.Arguments ?? new Dictionary<string, object?>(), null, true, mapsCall.Signature, null, part);
                    break;

                case InteractionFunctionResultContent functionResult:
                    yield return CreateToolPart("function_result", functionResult.CallId, functionResult.Name ?? "function", null, functionResult.Result, false, functionResult.Signature,
                        new Dictionary<string, object?> { ["interactions.is_error"] = functionResult.IsError }, part);
                    break;

                case InteractionCodeExecutionResultContent codeResult:
                    yield return CreateToolPart("code_execution_result", codeResult.CallId, "code_execution", null, codeResult.Result, true, codeResult.Signature,
                        new Dictionary<string, object?> { ["interactions.is_error"] = codeResult.IsError }, part);
                    break;

                case InteractionUrlContextResultContent urlResult:
                    yield return CreateToolPart("url_context_result", urlResult.CallId, "url_context", null, urlResult.Result, true, urlResult.Signature,
                        new Dictionary<string, object?> { ["interactions.is_error"] = urlResult.IsError }, part);
                    break;

                case InteractionGoogleSearchResultContent googleSearchResult:
                    yield return CreateToolPart("google_search_result", googleSearchResult.CallId, "google_search", null, googleSearchResult.Result, true, googleSearchResult.Signature,
                        new Dictionary<string, object?> { ["interactions.is_error"] = googleSearchResult.IsError }, part);
                    break;

                case InteractionMcpServerToolResultContent mcpResult:
                    yield return CreateToolPart("mcp_server_tool_result", mcpResult.CallId, mcpResult.Name ?? "mcp_server", null, mcpResult.Result, true, mcpResult.Signature,
                        new Dictionary<string, object?> { ["interactions.server_name"] = mcpResult.ServerName }, part);
                    break;

                case InteractionFileSearchResultContent fileSearchResult:
                    yield return CreateToolPart("file_search_result", fileSearchResult.CallId, "file_search", null, fileSearchResult.Result, true, fileSearchResult.Signature, null, part);
                    break;

                case InteractionGoogleMapsResultContent mapsResult:
                    yield return CreateToolPart("google_maps_result", mapsResult.CallId, "google_maps", null, mapsResult.Result, true, mapsResult.Signature, null, part);
                    break;
            }
        }

        AIFileContentPart CreateFilePart(string type, string? mimeType, object? data, string? uri, string? resolution, InteractionContent rawPart)
            => new()
            {
                Type = "file",
                MediaType = mimeType,
                Data = data,
                Metadata = new Dictionary<string, object?>
                {
                    ["interactions.content.type"] = type,
                    ["interactions.mime_type"] = mimeType,
                    ["interactions.uri"] = uri,
                    ["interactions.resolution"] = resolution,
                    ["interactions.raw"] = JsonSerializer.SerializeToElement(rawPart, Json)
                }
            };
    }

    private static InteractionContent? ToInteractionContent(AIContentPart part)
    {
        switch (part)
        {
            case AITextContentPart text:
                return new InteractionTextContent
                {
                    Text = text.Text,
                    Annotations = ExtractObject<List<InteractionAnnotation>>(text.Metadata, "interactions.annotations")
                };

            case AIReasoningContentPart reasoning:
            {
                var signature = ExtractValue<string>(reasoning.Metadata, "interactions.signature")
                                ?? ExtractThoughtSignatureFromProviderMetadata(reasoning.Metadata);

                return new InteractionThoughtContent
                {
                    Signature = signature,
                    Summary =
                    [
                        new InteractionTextContent
                        {
                            Text = reasoning.Text ?? string.Empty
                        }
                    ]
                };
            }

            case AIFileContentPart file:
                return ToInteractionFileContent(file);

            case AIToolCallContentPart tool:
                return ToInteractionToolContent(tool);

            default:
                return null;
        }
    }

    private static InteractionContent? ToInteractionFileContent(AIFileContentPart file)
    {
        var contentType = ExtractValue<string>(file.Metadata, "interactions.content.type");
        var mimeType = GetInteractionMimeType(file);
        var data = file.Data?.ToString();
        var uri = ExtractValue<string>(file.Metadata, "interactions.uri");

        switch (contentType ?? mimeType?.Split('/').FirstOrDefault())
        {
            case "image":
                return new InteractionImageContent
                {
                    MimeType = mimeType,
                    Data = IsHttpUrl(data) ? null : data,
                    Uri = uri ?? (IsHttpUrl(data) ? data : null),
                    Resolution = ExtractValue<string>(file.Metadata, "interactions.resolution")
                };

            case "audio":
                return new InteractionAudioContent
                {
                    MimeType = mimeType,
                    Data = IsHttpUrl(data) ? null : data,
                    Uri = uri ?? (IsHttpUrl(data) ? data : null),
                    Rate = ExtractValue<int?>(file.Metadata, "interactions.rate"),
                    Channels = ExtractValue<int?>(file.Metadata, "interactions.channels")
                };

            case "video":
                return new InteractionVideoContent
                {
                    MimeType = mimeType,
                    Data = IsHttpUrl(data) ? null : data,
                    Uri = uri ?? (IsHttpUrl(data) ? data : null),
                    Resolution = ExtractValue<string>(file.Metadata, "interactions.resolution")
                };

            case "document":
                return new InteractionDocumentContent
                {
                    MimeType = mimeType ?? "application/pdf",
                    Data = IsHttpUrl(data) ? null : data,
                    Uri = uri ?? (IsHttpUrl(data) ? data : null)
                };
        }

        if (mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new InteractionImageContent
            {
                MimeType = mimeType,
                Data = IsHttpUrl(data) ? null : data,
                Uri = uri ?? (IsHttpUrl(data) ? data : null)
            };
        }

        return !string.IsNullOrWhiteSpace(data)
            ? new InteractionTextContent { Text = data }
            : null;
    }

    private static InteractionContent ToInteractionToolContent(AIToolCallContentPart tool)
    {
        var type = ExtractValue<string>(tool.Metadata, "interactions.content.type");
        var signature = ExtractValue<string>(tool.Metadata, "interactions.signature");
        var serverName = ExtractValue<string>(tool.Metadata, "interactions.server_name");
        var searchType = ExtractValue<string>(tool.Metadata, "interactions.search_type");
        var isError = ExtractValue<bool?>(tool.Metadata, "interactions.is_error");

        return type switch
        {
            "code_execution_call" => new InteractionCodeExecutionCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionCodeExecutionCallArguments>(tool.Input)
            },
            "url_context_call" => new InteractionUrlContextCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionUrlContextCallArguments>(tool.Input)
            },
            "mcp_server_tool_call" => new InteractionMcpServerToolCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature,
                Name = tool.ToolName,
                ServerName = serverName,
                Arguments = CloneIfJsonElement(tool.Input)
            },
            "google_search_call" => new InteractionGoogleSearchCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature,
                SearchType = searchType,
                Arguments = DeserializeFromObject<InteractionGoogleSearchCallArguments>(tool.Input)
            },
            "file_search_call" => new InteractionFileSearchCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature
            },
            "google_maps_call" => new InteractionGoogleMapsCallContent
            {
                Id = tool.ToolCallId,
                Signature = signature,
                Arguments = DeserializeFromObject<InteractionGoogleMapsCallArguments>(tool.Input)
            },
            "function_result" => new InteractionFunctionResultContent
            {
                CallId = tool.ToolCallId,
                Name = tool.ToolName,
                Signature = signature,
                IsError = isError,
                Result = CloneIfJsonElement(tool.Output)
            },
            "code_execution_result" => new InteractionCodeExecutionResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                IsError = isError,
                Result = SerializePayload(tool.Output, string.Empty)
            },
            "url_context_result" => new InteractionUrlContextResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                IsError = isError,
                Result = DeserializeFromObject<List<InteractionUrlContextResult>>(tool.Output)
            },
            "google_search_result" => new InteractionGoogleSearchResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                IsError = isError,
                Result = DeserializeFromObject<List<InteractionGoogleSearchResult>>(tool.Output)
            },
            "mcp_server_tool_result" => new InteractionMcpServerToolResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                Name = tool.ToolName,
                ServerName = serverName,
                Result = CloneIfJsonElement(tool.Output)
            },
            "file_search_result" => new InteractionFileSearchResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                Result = DeserializeFromObject<List<InteractionFileSearchResult>>(tool.Output)
            },
            "google_maps_result" => new InteractionGoogleMapsResultContent
            {
                CallId = tool.ToolCallId,
                Signature = signature,
                Result = DeserializeFromObject<List<InteractionGoogleMapsResult>>(tool.Output)
            },
            _ when HasToolOutput(tool) => new InteractionFunctionResultContent
            {
                CallId = tool.ToolCallId,
                Name = tool.ToolName,
                Signature = signature,
                Result = CloneIfJsonElement(tool.Output),
                IsError = isError
            },
            _ => new InteractionFunctionCallContent
            {
                Id = tool.ToolCallId,
                Name = tool.ToolName,
                Signature = signature,
                Arguments = CloneIfJsonElement(tool.Input)
            }
        };
    }

    private static AIToolCallContentPart CreateToolPart(
        string type,
        string? id,
        string? toolName,
        object? input,
        object? output,
        bool providerExecuted,
        string? signature,
        Dictionary<string, object?>? extraMetadata,
        InteractionContent raw)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["interactions.content.type"] = type,
            ["interactions.signature"] = signature,
            ["interactions.raw"] = JsonSerializer.SerializeToElement(raw, Json)
        };

        if (extraMetadata is not null)
        {
            foreach (var entry in extraMetadata)
                metadata[entry.Key] = entry.Value;
        }

        return new AIToolCallContentPart
        {
            Type = type,
            ToolCallId = id ?? Guid.NewGuid().ToString("N"),
            ToolName = toolName,
            Title = toolName,
            Input = CloneIfJsonElement(input),
            Output = CloneIfJsonElement(output),
            State = HasMeaningfulValue(output) ? "output-available" : "input-available",
            ProviderExecuted = providerExecuted,
            Metadata = metadata
        };
    }

    private static AIToolDefinition ToUnifiedTool(InteractionTool tool)
        => tool switch
        {
            InteractionFunctionTool function => new AIToolDefinition
            {
                Name = function.Name ?? "function",
                Description = function.Description,
                InputSchema = CloneIfJsonElement(function.Parameters),
                Metadata = new Dictionary<string, object?>
                {
                    ["interactions.tool.type"] = "function",
                    ["interactions.tool.raw"] = JsonSerializer.SerializeToElement(tool, Json)
                }
            },
            _ => new AIToolDefinition
            {
                Name = GetToolName(tool),
                Description = null,
                InputSchema = null,
                Metadata = new Dictionary<string, object?>
                {
                    ["interactions.tool.type"] = tool.Type,
                    ["interactions.tool.raw"] = JsonSerializer.SerializeToElement(tool, Json)
                }
            }
        };

    private static InteractionTool ToInteractionTool(AIToolDefinition tool)
    {
        var raw = ExtractObject<InteractionTool>(tool.Metadata, "interactions.tool.raw");
        if (raw is not null)
            return raw;

        return (ExtractValue<string>(tool.Metadata, "interactions.tool.type") ?? "function") switch
        {
            "code_execution" => new InteractionCodeExecutionTool(),
            "url_context" => new InteractionUrlContextTool(),
            "computer_use" => new InteractionComputerUseTool(),
            "mcp_server" => new InteractionMcpServerTool { Name = tool.Name },
            "google_search" => new InteractionGoogleSearchTool(),
            "file_search" => new InteractionFileSearchTool(),
            "google_maps" => new InteractionGoogleMapsTool(),
            "retrieval" => new InteractionRetrievalTool(),
            _ => new InteractionFunctionTool
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = CloneIfJsonElement(tool.InputSchema)
            }
        };
    }

    private static string GetToolName(InteractionTool tool)
        => tool switch
        {
            InteractionFunctionTool function => function.Name ?? "function",
            InteractionMcpServerTool mcp => mcp.Name ?? "mcp_server",
            _ => tool.Type
        };
}
