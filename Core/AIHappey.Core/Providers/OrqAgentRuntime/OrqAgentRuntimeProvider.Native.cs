using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.ChatCompletions.Models;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OrqAgentRuntime;

public partial class OrqAgentRuntimeProvider
{
    private const string DeploymentsPath = "deployments";
    private const string InvokePath = "deployments/invoke";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> ChatCapableModelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chat",
        "completion",
        "vision"
    };

    private async Task<List<OrqDeployment>> ListDeploymentsInternalAsync(CancellationToken cancellationToken)
    {
        var deployments = new List<OrqDeployment>();
        string? startingAfter = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var relativeUrl = new StringBuilder(DeploymentsPath).Append("?limit=50");
            if (!string.IsNullOrWhiteSpace(startingAfter))
                relativeUrl.Append("&starting_after=").Append(Uri.EscapeDataString(startingAfter));

            using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl.ToString());
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException($"Orq Agents deployments error ({(int)response.StatusCode}): {error}");
            }

            var payload = await response.Content.ReadFromJsonAsync<OrqDeploymentListResponse>(Json, cancellationToken)
                          ?? throw new InvalidOperationException("Orq Agents returned an empty deployment list response.");

            if (payload.Data?.Count > 0)
                deployments.AddRange(payload.Data.Where(d => d is not null)!);

            if (!payload.HasMore || payload.Data is null || payload.Data.Count == 0)
                break;

            startingAfter = payload.Data.LastOrDefault()?.Id;
            if (string.IsNullOrWhiteSpace(startingAfter))
                break;
        }

        return deployments;
    }

    private async Task<OrqInvokeResponse> InvokeInternalAsync(OrqInvokeRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(request, Json);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InvokePath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Orq Agents invoke error ({(int)response.StatusCode}): {error}");
        }

        return await response.Content.ReadFromJsonAsync<OrqInvokeResponse>(Json, cancellationToken)
               ?? throw new InvalidOperationException("Orq Agents returned an empty invoke response.");
    }

    private async IAsyncEnumerable<OrqInvokeResponse> InvokeStreamingInternalAsync(
        OrqInvokeRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request.Stream = true;
        var json = JsonSerializer.Serialize(request, Json);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InvokePath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        httpRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Orq Agents invoke stream error ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var dataLines = new List<string>();

        static string? Flush(List<string> lines)
        {
            if (lines.Count == 0)
                return null;

            var payload = string.Join("\n", lines);
            lines.Clear();
            return string.IsNullOrWhiteSpace(payload) ? null : payload.Trim();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var payload = Flush(dataLines);
                if (payload is null)
                    continue;

                if (string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;

                OrqInvokeResponse? chunk = null;
                try
                {
                    chunk = JsonSerializer.Deserialize<OrqInvokeResponse>(payload, Json);
                }
                catch
                {
                    // Ignore malformed chunks and continue consuming the stream.
                }

                if (chunk is not null)
                    yield return chunk;

                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        var trailing = Flush(dataLines);
        if (string.IsNullOrWhiteSpace(trailing)
            || string.Equals(trailing, "[DONE]", StringComparison.OrdinalIgnoreCase))
            yield break;

        var finalChunk = JsonSerializer.Deserialize<OrqInvokeResponse>(trailing, Json);
        if (finalChunk is not null)
            yield return finalChunk;
    }

    private OrqInvokeRequest BuildInvokeRequest(ChatCompletionOptions options, bool stream)
    {
        var extraParams = new Dictionary<string, object?>();
        AddIfValue(extraParams, "temperature", options.Temperature);
        AddIfValue(extraParams, "responseFormat", options.ResponseFormat);
        AddIfValue(extraParams, "parallel_tool_calls", options.ParallelToolCalls);

        if (options.Tools?.Any() == true)
            extraParams["tools"] = options.Tools.Select(ToInvokeTool).ToArray();

        if (!string.IsNullOrWhiteSpace(options.ToolChoice))
            extraParams["tool_choice"] = options.ToolChoice;

        return new OrqInvokeRequest
        {
            Key = NormalizeModelKey(options.Model),
            Stream = stream,
            Messages = options.Messages?.Select(ToInvokeMessage).ToArray(),
            ExtraParams = extraParams.Count == 0 ? null : extraParams,
            InvokeOptions = BuildInvokeOptions(includeUsage: true, includeRetrievals: true)
        };
    }

    private OrqInvokeRequest BuildInvokeRequest(ResponseRequest options, bool stream)
    {
        var (nativeMetadata, requestMetadata) = ExtractResponseRequestMetadata(options.Metadata);

        var extraParams = nativeMetadata?.ExtraParams is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(nativeMetadata.ExtraParams, StringComparer.OrdinalIgnoreCase);

        AddIfValue(extraParams, "temperature", options.Temperature);
        AddIfValue(extraParams, "topP", options.TopP);
        AddIfValue(extraParams, "maxTokens", options.MaxOutputTokens);
        AddIfValue(extraParams, "responseFormat", options.Text);
        AddIfValue(extraParams, "parallel_tool_calls", options.ParallelToolCalls);
        AddIfValue(extraParams, "tool_choice", options.ToolChoice);

        if (options.Tools?.Count > 0)
            extraParams["tools"] = options.Tools.Select(ToInvokeTool).ToArray();

        return new OrqInvokeRequest
        {
            Key = NormalizeModelKey(options.Model ?? throw new ArgumentException("Model is required.", nameof(options))),
            Stream = stream,
            Messages = BuildInvokeMessagesFromResponseRequest(options),
            PrefixMessages = nativeMetadata?.PrefixMessages,
            Inputs = nativeMetadata?.Inputs,
            Context = nativeMetadata?.Context,
            Identity = nativeMetadata?.Identity,
            FileIds = nativeMetadata?.FileIds,
            Metadata = MergeMetadata(requestMetadata, nativeMetadata?.Metadata),
            ExtraParams = extraParams.Count == 0 ? null : extraParams,
            Documents = nativeMetadata?.Documents,
            InvokeOptions = MergeInvokeOptions(BuildInvokeOptions(includeUsage: true, includeRetrievals: true), nativeMetadata?.InvokeOptions),
            Thread = nativeMetadata?.Thread,
            KnowledgeFilter = nativeMetadata?.KnowledgeFilter
        };
    }

    private OrqInvokeRequest BuildInvokeRequest(ChatRequest request, bool stream)
    {
        var nativeMetadata = request.GetProviderMetadata<OrqAgentsRequestMetadata>(GetIdentifier()) ?? new OrqAgentsRequestMetadata();
        var extraParams = nativeMetadata.ExtraParams is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(nativeMetadata.ExtraParams, StringComparer.OrdinalIgnoreCase);

        AddIfValue(extraParams, "temperature", request.Temperature);
        AddIfValue(extraParams, "topP", request.TopP);
        AddIfValue(extraParams, "maxTokens", request.MaxOutputTokens);
        AddIfValue(extraParams, "responseFormat", request.ResponseFormat);
        AddIfValue(extraParams, "tool_choice", request.ToolChoice);
        AddIfValue(extraParams, "max_tool_calls", request.MaxToolCalls);

        if (request.Tools?.Count > 0)
            extraParams["tools"] = request.Tools.Select(ToInvokeTool).ToArray();

        return new OrqInvokeRequest
        {
            Key = NormalizeModelKey(request.Model),
            Stream = stream,
            Messages = BuildInvokeMessagesFromUiMessages(request.Messages),
            PrefixMessages = nativeMetadata.PrefixMessages,
            Inputs = nativeMetadata.Inputs,
            Context = nativeMetadata.Context,
            Identity = nativeMetadata.Identity,
            FileIds = nativeMetadata.FileIds,
            Metadata = nativeMetadata.Metadata,
            ExtraParams = extraParams.Count == 0 ? null : extraParams,
            Documents = nativeMetadata.Documents,
            InvokeOptions = MergeInvokeOptions(BuildInvokeOptions(includeUsage: true, includeRetrievals: true), nativeMetadata.InvokeOptions),
            Thread = nativeMetadata.Thread,
            KnowledgeFilter = nativeMetadata.KnowledgeFilter
        };
    }

    private ChatCompletion ToChatCompletion(ChatCompletionOptions options, OrqInvokeResponse response)
        => new()
        {
            Id = string.IsNullOrWhiteSpace(response.Id) ? Guid.NewGuid().ToString("n") : response.Id!,
            Object = "chat.completion",
            Created = ParseUnixTime(response.Created),
            Model = options.Model,
            Choices = (response.Choices ?? [])
                .Select(choice => new
                {
                    index = choice.Index,
                    message = BuildChatCompletionMessage(choice.Message),
                    finish_reason = DetermineChoiceFinishReason(choice)
                })
                .Cast<object>()
                .ToArray(),
            Usage = ToUntyped(response.Usage)
        };

    private ResponseResult ToResponseResult(
        ResponseRequest options,
        OrqInvokeResponse response,
        long? overrideCreatedAt = null,
        string? fallbackText = null,
        object? overrideUsage = null,
        string? failureMessage = null)
    {
        var createdAt = overrideCreatedAt ?? ParseUnixTime(response.Created);
        var completedAt = ParseUnixTime(response.Finalized, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var output = BuildResponseOutput(response, fallbackText);
        var status = DetermineResponseStatus(response, failureMessage, output.Count > 0);
        var (nativeMetadata, requestMetadata) = ExtractResponseRequestMetadata(options.Metadata);

        return new ResponseResult
        {
            Id = string.IsNullOrWhiteSpace(response.Id) ? Guid.NewGuid().ToString("n") : response.Id!,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase) ? null : completedAt,
            Status = status,
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? NormalizeModelKey(response.Model ?? string.Empty),
            Temperature = options.Temperature,
            Output = output,
            Usage = overrideUsage ?? ToUntyped(response.Usage),
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Error = BuildResponseError(response, failureMessage, status),
            Metadata = BuildResponseMetadata(requestMetadata, nativeMetadata, response)
        };
    }

    private ResponseResult CreateInProgressResponse(ResponseRequest options, string responseId, long createdAt)
    {
        var (_, requestMetadata) = ExtractResponseRequestMetadata(options.Metadata);

        return new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = requestMetadata,
            Output = []
        };
    }

    private ResponseResult BuildSyntheticResponseResult(
        ResponseRequest options,
        string responseId,
        long createdAt,
        string text,
        object? usage,
        string? failureMessage)
    {
        var (_, requestMetadata) = ExtractResponseRequestMetadata(options.Metadata);
        var status = string.IsNullOrWhiteSpace(failureMessage) ? "completed" : "failed";

        return new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = status,
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Usage = usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Error = string.IsNullOrWhiteSpace(failureMessage)
                ? null
                : new ResponseResultError
                {
                    Code = "orqagents_stream_error",
                    Message = failureMessage
                },
            Metadata = requestMetadata,
            Output = string.IsNullOrWhiteSpace(text)
                ? []
                :
                [
                    new
                    {
                        id = $"msg_{responseId}",
                        type = "message",
                        role = "assistant",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text
                            }
                        }
                    }
                ]
        };
    }

    private static Dictionary<string, object?>? MergeMetadata(
        Dictionary<string, object?>? left,
        Dictionary<string, object?>? right)
    {
        if ((left is null || left.Count == 0) && (right is null || right.Count == 0))
            return null;

        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (left is not null)
        {
            foreach (var kvp in left)
                merged[kvp.Key] = kvp.Value;
        }

        if (right is not null)
        {
            foreach (var kvp in right)
                merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    private static Dictionary<string, object?> MergeInvokeOptions(
        Dictionary<string, object?> initial,
        Dictionary<string, object?>? overrideOptions)
    {
        if (overrideOptions is null || overrideOptions.Count == 0)
            return initial;

        foreach (var kvp in overrideOptions)
            initial[kvp.Key] = kvp.Value;

        return initial;
    }

    private static Dictionary<string, object?> BuildInvokeOptions(bool includeUsage, bool includeRetrievals)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["include_usage"] = includeUsage,
            ["include_retrievals"] = includeRetrievals
        };

    private object[] BuildInvokeMessagesFromResponseRequest(ResponseRequest request)
    {
        var messages = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new
            {
                role = "system",
                content = request.Instructions
            });
        }

        if (request.Input?.IsText == true)
        {
            messages.Add(new
            {
                role = "user",
                content = request.Input.Text ?? string.Empty
            });
        }

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            foreach (var item in request.Input.Items)
            {
                if (item is not ResponseInputMessage message)
                    continue;

                var content = ToInvokeResponseMessageContent(message.Content);
                if (content is null)
                    continue;

                messages.Add(new
                {
                    role = message.Role switch
                    {
                        ResponseRole.System => "system",
                        ResponseRole.Developer => "developer",
                        ResponseRole.Assistant => "assistant",
                        _ => "user"
                    },
                    content,
                    id = message.Id,
                    status = message.Status
                });
            }
        }

        if (messages.Count == 0)
        {
            messages.Add(new
            {
                role = "user",
                content = string.Empty
            });
        }

        return messages.ToArray();
    }

    private object[] BuildInvokeMessagesFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var result = new List<object>();

        foreach (var message in messages ?? [])
        {
            var role = message.Role switch
            {
                Role.system => "system",
                Role.assistant => "assistant",
                _ => "user"
            };

            var contentParts = new List<object>();
            var toolCalls = new List<object>();
            var toolOutputs = new List<object>();

            foreach (var part in message.Parts ?? [])
            {
                switch (part)
                {
                    case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                        contentParts.Add(new { type = "text", text = text.Text });
                        break;

                    case ReasoningUIPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        contentParts.Add(new { type = "text", text = reasoning.Text });
                        break;

                    case FileUIPart file:
                        contentParts.Add(ToInvokeFileContentPart(file));
                        break;

                    case ToolInvocationPart invocation:
                        var toolName = !string.IsNullOrWhiteSpace(invocation.Title)
                            ? invocation.Title!
                            : string.IsNullOrWhiteSpace(invocation.Type) ? "tool" : invocation.Type;

                        toolCalls.Add(new
                        {
                            id = invocation.ToolCallId,
                            type = "function",
                            function = new
                            {
                                name = toolName,
                                arguments = JsonSerializer.Serialize(invocation.Input, Json)
                            }
                        });

                        if (invocation.Output is not null)
                        {
                            toolOutputs.Add(new
                            {
                                role = "tool",
                                tool_call_id = invocation.ToolCallId,
                                content = JsonSerializer.Serialize(invocation.Output, Json)
                            });
                        }
                        break;
                }
            }

            if (contentParts.Count > 0 || toolCalls.Count > 0)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["content"] = contentParts.Count == 0 ? null : (contentParts.Count == 1 && contentParts[0] is not null ? new[] { contentParts[0] } : contentParts.ToArray())
                };

                if (toolCalls.Count > 0)
                    payload["tool_calls"] = toolCalls.ToArray();

                result.Add(payload);
            }

            if (toolOutputs.Count > 0)
                result.AddRange(toolOutputs);
        }

        if (result.Count == 0)
        {
            result.Add(new
            {
                role = "user",
                content = new[] { new { type = "text", text = string.Empty } }
            });
        }

        return result.ToArray();
    }

    private static object ToInvokeMessage(ChatMessage message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
            ["content"] = ToInvokeChatContent(message.Content)
        };

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            payload["tool_call_id"] = message.ToolCallId;

        if (message.ToolCalls?.Any() == true)
            payload["tool_calls"] = message.ToolCalls.Select(ToInvokeToolCall).ToArray();

        return payload;
    }

    private static object? ToInvokeChatContent(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<object>();

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    var text = part.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add(new { type = "text", text });
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                {
                    if (TryConvertJsonElement(part, out var rawPart))
                        parts.Add(rawPart!);
                    continue;
                }

                var type = part.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                switch (type)
                {
                    case "text":
                        parts.Add(new
                        {
                            type = "text",
                            text = part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                                ? textEl.GetString()
                                : string.Empty
                        });
                        break;

                    case "image_url":
                        parts.Add(new
                        {
                            type = "image_url",
                            image_url = ToUntyped(part.GetProperty("image_url"))
                        });
                        break;

                    case "file":
                        parts.Add(new
                        {
                            type = "file",
                            file = ToUntyped(part.GetProperty("file"))
                        });
                        break;

                    case "input_audio":
                        parts.Add(ToUntyped(part)!);
                        break;

                    default:
                        if (TryConvertJsonElement(part, out var raw))
                            parts.Add(raw!);
                        break;
                }
            }

            return parts;
        }

        return ToUntyped(content);
    }

    private static object? ToInvokeResponseMessageContent(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text;

        if (!content.IsParts || content.Parts is null)
            return null;

        var parts = new List<object>();

        foreach (var part in content.Parts)
        {
            var element = JsonSerializer.SerializeToElement(part, ResponseJson.Default);
            var type = element.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;

            switch (type)
            {
                case "input_text":
                    parts.Add(new
                    {
                        type = "text",
                        text = element.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                            ? textEl.GetString()
                            : string.Empty
                    });
                    break;

                case "input_image":
                    parts.Add(new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = element.TryGetProperty("image_url", out var imageUrlEl) && imageUrlEl.ValueKind == JsonValueKind.String
                                ? imageUrlEl.GetString()
                                : element.TryGetProperty("file_id", out var fileIdEl) && fileIdEl.ValueKind == JsonValueKind.String
                                    ? fileIdEl.GetString()
                                    : null,
                            detail = element.TryGetProperty("detail", out var detailEl) && detailEl.ValueKind == JsonValueKind.String
                                ? detailEl.GetString()
                                : null
                        }
                    });
                    break;

                case "input_file":
                    parts.Add(new
                    {
                        type = "file",
                        file = ToUntyped(element)
                    });
                    break;
            }
        }

        return parts.Count == 0 ? null : parts;
    }

    private static object ToInvokeTool(object tool)
    {
        if (tool is Tool typedTool)
        {
            return new
            {
                type = "function",
                function = new
                {
                    name = typedTool.Name,
                    description = typedTool.Description,
                    parameters = typedTool.InputSchema is null
                            ? new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>(),
                                required = Array.Empty<string>(),
                                additionalProperties = false
                            }
                            : new
                            {
                                type = typedTool.InputSchema.Type,
                                properties = typedTool.InputSchema.Properties ?? new Dictionary<string, object>(),
                                required = typedTool.InputSchema.Required?.ToArray() ?? Array.Empty<string>(),
                                additionalProperties = false
                            }
                },
                display_name = typedTool.Title
            };
        }

        if (tool is ResponseToolDefinition responseTool)
            return ToInvokeTool(responseTool);

        return tool;
    }

    private static object ToInvokeTool(ResponseToolDefinition tool)
    {
        if (!string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase))
            return new { type = tool.Type };

        string name = "function";
        string? description = null;
        object? parameters = null;

        if (tool.Extra is not null)
        {
            if (tool.Extra.TryGetValue("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                name = nameEl.GetString() ?? name;

            if (tool.Extra.TryGetValue("description", out var descriptionEl) && descriptionEl.ValueKind == JsonValueKind.String)
                description = descriptionEl.GetString();

            if (tool.Extra.TryGetValue("parameters", out var parametersEl)
                && parametersEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parameters = ToUntyped(parametersEl);
            }
        }

        parameters ??= new { type = "object", properties = new Dictionary<string, object>(), required = Array.Empty<string>(), additionalProperties = false };

        return new
        {
            type = "function",
            function = new
            {
                name,
                description,
                parameters
            }
        };
    }

    private static object ToInvokeToolCall(object toolCall)
    {
        if (toolCall is null)
            return new { type = "function", function = new { name = "function", arguments = "{}" } };

        var element = JsonSerializer.SerializeToElement(toolCall, Json);

        return new
        {
            id = element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null,
            index = element.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number ? indexEl.GetInt32() : 0,
            type = "function",
            function = new
            {
                name = element.TryGetProperty("function", out var functionEl)
                    && functionEl.ValueKind == JsonValueKind.Object
                    && functionEl.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String
                        ? nameEl.GetString()
                        : "function",
                arguments = element.TryGetProperty("function", out functionEl)
                    && functionEl.ValueKind == JsonValueKind.Object
                    && functionEl.TryGetProperty("arguments", out var argsEl)
                    && argsEl.ValueKind == JsonValueKind.String
                        ? argsEl.GetString()
                        : "{}"
            }
        };
    }

    private static object ToInvokeFileContentPart(FileUIPart file)
    {
        if (file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                type = "image_url",
                image_url = new
                {
                    url = file.Url
                }
            };
        }

        return new
        {
            type = "file",
            file = file.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? new
                {
                    file_data = (string?)file.Url,
                    uri = (string?)null,
                    mimeType = file.MediaType
                }
                : new
                {
                    file_data = (string?)null,
                    uri = (string?)file.Url,
                    mimeType = file.MediaType
                }
        };
    }

    private (OrqAgentsRequestMetadata? NativeMetadata, Dictionary<string, object?>? RequestMetadata) ExtractResponseRequestMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
            return (null, null);

        var requestMetadata = new Dictionary<string, object?>(metadata, StringComparer.OrdinalIgnoreCase);
        OrqAgentsRequestMetadata? nativeMetadata = null;

        if (requestMetadata.TryGetValue(GetIdentifier(), out var raw) && raw is not null)
        {
            try
            {
                nativeMetadata = JsonSerializer.SerializeToElement(raw, Json).Deserialize<OrqAgentsRequestMetadata>(Json);
            }
            catch
            {
                nativeMetadata = null;
            }

            requestMetadata.Remove(GetIdentifier());
        }

        return (nativeMetadata, requestMetadata.Count == 0 ? null : requestMetadata);
    }

    private static object BuildChatCompletionMessage(OrqInvokeMessage? message)
    {
        var payload = new Dictionary<string, object?>
        {
            ["role"] = message?.Role ?? "assistant",
            ["content"] = ExtractMessageText(message)
        };

        if (message?.ToolCalls?.Count > 0)
            payload["tool_calls"] = message.ToolCalls.Select(MapToolCallForResponse).ToArray();

        if (!string.IsNullOrWhiteSpace(message?.Reasoning))
            payload["reasoning"] = message.Reasoning;

        if (!string.IsNullOrWhiteSpace(message?.ReasoningSignature))
            payload["reasoning_signature"] = message.ReasoningSignature;

        if (!string.IsNullOrWhiteSpace(message?.RedactedReasoning))
            payload["redacted_reasoning"] = message.RedactedReasoning;

        return payload;
    }

    private List<object> BuildResponseOutput(OrqInvokeResponse response, string? fallbackText = null)
    {
        var output = new List<object>();

        foreach (var choice in response.Choices ?? [])
        {
            var message = choice.Message;
            if (message is null)
                continue;

            foreach (var toolCall in message.ToolCalls ?? [])
            {
                output.Add(new
                {
                    id = string.IsNullOrWhiteSpace(toolCall.Id) ? $"tool_{Guid.NewGuid():N}" : toolCall.Id,
                    type = "tool_call",
                    call_id = toolCall.Id,
                    tool_name = toolCall.Function?.Name,
                    arguments = ParseJsonStringOrValue(toolCall.Function?.Arguments)
                });
            }

            var content = BuildResponseMessageContent(message, fallbackText);
            if (content.Count == 0)
                continue;

            output.Add(new
            {
                id = $"msg_{response.Id}_{choice.Index}",
                type = "message",
                role = message.Role ?? "assistant",
                content = content.ToArray()
            });
        }

        if (output.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
        {
            output.Add(new
            {
                id = $"msg_{response.Id}",
                type = "message",
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = fallbackText
                    }
                }
            });
        }

        return output;
    }

    private List<object> BuildResponseMessageContent(OrqInvokeMessage message, string? fallbackText = null)
    {
        var content = new List<object>();

        if (message.Content.ValueKind == JsonValueKind.String)
        {
            var text = message.Content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                content.Add(new { type = "output_text", text });
        }
        else if (message.Content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in message.Content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                    ? typeEl.GetString()
                    : null;

                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
                    && part.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        content.Add(new { type = "output_text", text });
                    continue;
                }

                if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase)
                    && part.TryGetProperty("url", out var urlEl)
                    && urlEl.ValueKind == JsonValueKind.String)
                {
                    content.Add(new { type = "output_image", url = urlEl.GetString() });
                    continue;
                }

                if (TryConvertJsonElement(part, out var raw))
                    content.Add(raw!);
            }
        }

        if (content.Count == 0 && !string.IsNullOrWhiteSpace(fallbackText))
            content.Add(new { type = "output_text", text = fallbackText });

        if (!string.IsNullOrWhiteSpace(message.Reasoning))
            content.Add(new { type = "reasoning", text = message.Reasoning });

        if (!string.IsNullOrWhiteSpace(message.RedactedReasoning))
            content.Add(new { type = "redacted_reasoning", data = message.RedactedReasoning });

        return content;
    }

    private static object MapToolCallForResponse(OrqInvokeToolCall toolCall)
        => new
        {
            id = toolCall.Id,
            index = toolCall.Index,
            type = "function",
            function = new
            {
                name = toolCall.Function?.Name,
                arguments = toolCall.Function?.Arguments ?? "{}"
            }
        };

    private static List<object> BuildStreamingToolCalls(OrqInvokeMessage? message, Dictionary<string, string> emittedArguments)
    {
        var updates = new List<object>();

        foreach (var toolCall in message?.ToolCalls ?? [])
        {
            var toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                ? $"tool_{Guid.NewGuid():N}"
                : toolCall.Id!;
            var deltaArguments = GetIncrementalDelta(emittedArguments, toolCallId, toolCall.Function?.Arguments);

            if (string.IsNullOrEmpty(deltaArguments)
                && !string.IsNullOrWhiteSpace(toolCall.Function?.Name)
                && emittedArguments.ContainsKey(toolCallId))
                continue;

            updates.Add(new
            {
                index = toolCall.Index,
                id = toolCall.Id,
                type = "function",
                function = new
                {
                    name = toolCall.Function?.Name,
                    arguments = deltaArguments
                }
            });
        }

        return updates;
    }

    private static string ExtractPrimaryOutputText(OrqInvokeResponse response, string? fallback = null)
    {
        foreach (var choice in response.Choices ?? [])
        {
            var text = ExtractMessageText(choice.Message);
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return fallback ?? string.Empty;
    }

    private static string ExtractMessageText(OrqInvokeMessage? message)
    {
        if (message is null)
            return string.Empty;

        if (message.Content.ValueKind == JsonValueKind.String)
            return message.Content.GetString() ?? string.Empty;

        if (message.Content.ValueKind == JsonValueKind.Array)
        {
            var lines = new List<string>();

            foreach (var part in message.Content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    var text = part.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add(text);
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                    continue;

                if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add(text);
                }
            }

            return string.Join("\n", lines);
        }

        return string.Empty;
    }

    private Dictionary<string, object?>? BuildResponseMetadata(
        Dictionary<string, object?>? requestMetadata,
        OrqAgentsRequestMetadata? nativeMetadata,
        OrqInvokeResponse response)
    {
        var merged = requestMetadata is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(requestMetadata, StringComparer.OrdinalIgnoreCase);

        if (nativeMetadata?.Metadata is not null)
        {
            foreach (var kvp in nativeMetadata.Metadata)
                merged[kvp.Key] = kvp.Value;
        }

        var orqMetadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        AddIfValue(orqMetadata, "object", response.Object);
        AddIfValue(orqMetadata, "provider", response.Provider);
        AddIfValue(orqMetadata, "backend_model", response.Model);
        AddIfValue(orqMetadata, "integration_id", response.IntegrationId);
        AddIfValue(orqMetadata, "system_fingerprint", response.SystemFingerprint);
        AddIfValue(orqMetadata, "is_final", response.IsFinal);

        if (response.Telemetry is not null)
        {
            orqMetadata["telemetry"] = new Dictionary<string, object?>
            {
                ["trace_id"] = response.Telemetry.TraceId,
                ["span_id"] = response.Telemetry.SpanId
            };
        }

        if (response.Retrievals?.Count > 0)
        {
            orqMetadata["retrievals"] = response.Retrievals.Select(retrieval => new Dictionary<string, object?>
            {
                ["id"] = retrieval.Id,
                ["document"] = retrieval.Document,
                ["metadata"] = new Dictionary<string, object?>
                {
                    ["file_name"] = retrieval.Metadata?.FileName,
                    ["page_number"] = retrieval.Metadata?.PageNumber,
                    ["file_type"] = retrieval.Metadata?.FileType,
                    ["rerank_score"] = retrieval.Metadata?.RerankScore,
                    ["search_score"] = retrieval.Metadata?.SearchScore
                }
            }).ToArray();
        }

        if (TryConvertJsonElement(response.ProviderResponse, out var providerResponse))
            orqMetadata["provider_response"] = providerResponse;

        if (orqMetadata.Count > 0)
            merged["orq"] = orqMetadata;

        return merged.Count == 0 ? null : merged;
    }

    private static ResponseResultError? BuildResponseError(OrqInvokeResponse response, string? failureMessage, string status)
    {
        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return null;

        return new ResponseResultError
        {
            Code = response.Code ?? "orqagents_error",
            Message = failureMessage ?? response.Message ?? "Orq Agents request failed."
        };
    }

    private static Dictionary<string, object>? BuildRetrievalProviderMetadata(OrqInvokeRetrieval retrieval)
    {
        var metadata = new Dictionary<string, object>();

        if (!string.IsNullOrWhiteSpace(retrieval.Document))
            metadata["document"] = retrieval.Document!;
        if (!string.IsNullOrWhiteSpace(retrieval.Metadata?.FileName))
            metadata["file_name"] = retrieval.Metadata.FileName!;
        if (!string.IsNullOrWhiteSpace(retrieval.Metadata?.FileType))
            metadata["file_type"] = retrieval.Metadata.FileType!;
        if (retrieval.Metadata?.PageNumber is not null)
            metadata["page_number"] = retrieval.Metadata.PageNumber.Value;
        if (retrieval.Metadata?.SearchScore is not null)
            metadata["search_score"] = retrieval.Metadata.SearchScore.Value;
        if (retrieval.Metadata?.RerankScore is not null)
            metadata["rerank_score"] = retrieval.Metadata.RerankScore.Value;

        return metadata.Count == 0 ? null : metadata;
    }

    private static Dictionary<string, object>? BuildUiMessageMetadata(OrqInvokeResponse? response)
    {
        if (response is null)
            return null;

        var metadata = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(response.Id))
            metadata["id"] = response.Id!;
        if (!string.IsNullOrWhiteSpace(response.Model))
            metadata["backendModel"] = response.Model!;
        if (!string.IsNullOrWhiteSpace(response.Provider))
            metadata["provider"] = response.Provider!;

        if (TryConvertJsonElement(response.Usage, out var usage))
            metadata["usage"] = usage!;

        return metadata.Count == 0 ? null : metadata;
    }

    private static bool TryConvertJsonElement(JsonElement element, out object? value)
    {
        value = null;
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return false;

        value = ToUntyped(element);
        return true;
    }

    private static object? ToUntyped(JsonElement element)
        => element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : JsonSerializer.Deserialize<object>(element.GetRawText(), Json);

    private static object? ParseJsonStringOrValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        try
        {
            return JsonSerializer.Deserialize<object>(raw, Json);
        }
        catch
        {
            return raw;
        }
    }

    private bool IsChatCapableDeployment(OrqDeployment deployment)
        => deployment.PromptConfig?.ModelType is { } modelType
           && ChatCapableModelTypes.Contains(modelType);

    private static IEnumerable<string> BuildDeploymentTags(OrqDeployment deployment)
    {
        var tags = new List<string> { "agent", "deployment" };

        if (!string.IsNullOrWhiteSpace(deployment.PromptConfig?.ModelType))
            tags.Add(deployment.PromptConfig.ModelType!);
        if (!string.IsNullOrWhiteSpace(deployment.PromptConfig?.Provider))
            tags.Add(deployment.PromptConfig.Provider!);
        if (!string.IsNullOrWhiteSpace(deployment.Version))
            tags.Add($"version:{deployment.Version}");

        return tags;
    }

    private static string MapDeploymentModelType(string? modelType)
        => modelType?.ToLowerInvariant() switch
        {
            "image" => "image",
            "embedding" => "embedding",
            "tts" => "speech",
            "stt" => "transcription",
            "rerank" => "reranking",
            _ => "language"
        };

    private string NormalizeModelKey(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        if (!model.Contains('/', StringComparison.Ordinal))
            return model;

        var split = model.SplitModelId();
        return string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
            ? split.Model
            : model;
    }

    private static long ParseUnixTime(string? value, long? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (long.TryParse(value, out var unix))
            return unix;

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToUnixTimeSeconds()
            : fallback ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static string DetermineChoiceFinishReason(OrqInvokeChoice choice)
    {
        if (!string.IsNullOrWhiteSpace(choice.FinishReason))
            return choice.FinishReason!;

        if (choice.Message?.ToolCalls?.Count > 0)
            return "tool_calls";

        return "stop";
    }

    private static string DetermineFinishReason(OrqInvokeResponse response, string fallback = "stop")
    {
        foreach (var choice in response.Choices ?? [])
        {
            if (!string.IsNullOrWhiteSpace(choice.FinishReason))
                return choice.FinishReason!;

            if (choice.Message?.ToolCalls?.Count > 0)
                return "tool_calls";
        }

        return !string.IsNullOrWhiteSpace(response.Code) || !string.IsNullOrWhiteSpace(response.Message)
            ? "error"
            : fallback;
    }

    private static bool HasTerminalChoice(OrqInvokeResponse response)
        => (response.Choices ?? []).Any(choice => !string.IsNullOrWhiteSpace(choice.FinishReason));

    private static string DetermineResponseStatus(OrqInvokeResponse response, string? failureMessage, bool hasOutput)
    {
        if (!string.IsNullOrWhiteSpace(failureMessage) || !string.IsNullOrWhiteSpace(response.Code))
            return "failed";

        if (response.IsFinal || HasTerminalChoice(response) || hasOutput)
            return "completed";

        return "in_progress";
    }

    private static void AddIfValue(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is null)
            return;

        target[key] = value;
    }

    private static string GetIncrementalDelta(IDictionary<string, string> state, string key, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return string.Empty;

        if (!state.TryGetValue(key, out var previous))
        {
            state[key] = candidate;
            return candidate;
        }

        if (string.Equals(previous, candidate, StringComparison.Ordinal))
            return string.Empty;

        if (candidate.StartsWith(previous, StringComparison.Ordinal))
        {
            state[key] = candidate;
            return candidate[previous.Length..];
        }

        if (previous.EndsWith(candidate, StringComparison.Ordinal))
            return string.Empty;

        state[key] = previous + candidate;
        return candidate;
    }

    private sealed class OrqDeploymentListResponse
    {
        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("data")]
        public List<OrqDeployment>? Data { get; set; }

        [JsonPropertyName("has_more")]
        public bool HasMore { get; set; }
    }

    private sealed class OrqDeployment
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("created")]
        public string? Created { get; set; }

        [JsonPropertyName("updated")]
        public string? Updated { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("prompt_config")]
        public OrqPromptConfig? PromptConfig { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class OrqPromptConfig
    {
        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("model_type")]
        public string? ModelType { get; set; }
    }

    private sealed class OrqInvokeRequest
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = default!;

        [JsonPropertyName("stream")]
        public bool? Stream { get; set; }

        [JsonPropertyName("inputs")]
        public Dictionary<string, object?>? Inputs { get; set; }

        [JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        [JsonPropertyName("prefix_messages")]
        public IEnumerable<object>? PrefixMessages { get; set; }

        [JsonPropertyName("messages")]
        public IEnumerable<object>? Messages { get; set; }

        [JsonPropertyName("identity")]
        public object? Identity { get; set; }

        [JsonPropertyName("file_ids")]
        public IEnumerable<string>? FileIds { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object?>? Metadata { get; set; }

        [JsonPropertyName("extra_params")]
        public Dictionary<string, object?>? ExtraParams { get; set; }

        [JsonPropertyName("documents")]
        public IEnumerable<object>? Documents { get; set; }

        [JsonPropertyName("invoke_options")]
        public Dictionary<string, object?>? InvokeOptions { get; set; }

        [JsonPropertyName("thread")]
        public object? Thread { get; set; }

        [JsonPropertyName("knowledge_filter")]
        public object? KnowledgeFilter { get; set; }
    }

    private sealed class OrqAgentsRequestMetadata
    {
        [JsonPropertyName("inputs")]
        public Dictionary<string, object?>? Inputs { get; set; }

        [JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

        [JsonPropertyName("identity")]
        public object? Identity { get; set; }

        [JsonPropertyName("file_ids")]
        public List<string>? FileIds { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object?>? Metadata { get; set; }

        [JsonPropertyName("extra_params")]
        public Dictionary<string, object?>? ExtraParams { get; set; }

        [JsonPropertyName("documents")]
        public List<object>? Documents { get; set; }

        [JsonPropertyName("invoke_options")]
        public Dictionary<string, object?>? InvokeOptions { get; set; }

        [JsonPropertyName("thread")]
        public object? Thread { get; set; }

        [JsonPropertyName("knowledge_filter")]
        public object? KnowledgeFilter { get; set; }

        [JsonPropertyName("prefix_messages")]
        public List<object>? PrefixMessages { get; set; }
    }

    private sealed class OrqInvokeResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("created")]
        public string? Created { get; set; }

        [JsonPropertyName("object")]
        public string? Object { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("is_final")]
        public bool IsFinal { get; set; }

        [JsonPropertyName("integration_id")]
        public string? IntegrationId { get; set; }

        [JsonPropertyName("telemetry")]
        public OrqInvokeTelemetry? Telemetry { get; set; }

        [JsonPropertyName("finalized")]
        public string? Finalized { get; set; }

        [JsonPropertyName("system_fingerprint")]
        public string? SystemFingerprint { get; set; }

        [JsonPropertyName("retrievals")]
        public List<OrqInvokeRetrieval>? Retrievals { get; set; }

        [JsonPropertyName("provider_response")]
        public JsonElement ProviderResponse { get; set; }

        [JsonPropertyName("usage")]
        public JsonElement Usage { get; set; }

        [JsonPropertyName("choices")]
        public List<OrqInvokeChoice>? Choices { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class OrqInvokeTelemetry
    {
        [JsonPropertyName("trace_id")]
        public string? TraceId { get; set; }

        [JsonPropertyName("span_id")]
        public string? SpanId { get; set; }
    }

    private sealed class OrqInvokeRetrieval
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("document")]
        public string? Document { get; set; }

        [JsonPropertyName("metadata")]
        public OrqInvokeRetrievalMetadata? Metadata { get; set; }
    }

    private sealed class OrqInvokeRetrievalMetadata
    {
        [JsonPropertyName("file_name")]
        public string? FileName { get; set; }

        [JsonPropertyName("page_number")]
        public int? PageNumber { get; set; }

        [JsonPropertyName("file_type")]
        public string? FileType { get; set; }

        [JsonPropertyName("rerank_score")]
        public double? RerankScore { get; set; }

        [JsonPropertyName("search_score")]
        public double? SearchScore { get; set; }
    }

    private sealed class OrqInvokeChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public OrqInvokeMessage? Message { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class OrqInvokeMessage
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("content")]
        public JsonElement Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OrqInvokeToolCall>? ToolCalls { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; set; }

        [JsonPropertyName("reasoning_signature")]
        public string? ReasoningSignature { get; set; }

        [JsonPropertyName("redacted_reasoning")]
        public string? RedactedReasoning { get; set; }
    }

    private sealed class OrqInvokeToolCall
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("function")]
        public OrqInvokeFunction? Function { get; set; }
    }

    private sealed class OrqInvokeFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; set; }
    }
}
