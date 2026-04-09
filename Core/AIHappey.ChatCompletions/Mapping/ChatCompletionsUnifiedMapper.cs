using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static class ChatCompletionsUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private static readonly HashSet<string> MappedRequestFields =
    [
        "model",
        "temperature",
        "top_p",
        "max_completion_tokens",
        "max_tokens",
        "stream",
        "parallel_tool_calls",
        "tool_choice",
        "response_format",
        "tools",
        "messages",
        "store"
    ];

    public static AIRequest ToUnifiedRequest(this ChatCompletionOptions request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(request, Json);

        return raw.ToUnifiedRequest(providerId);
    }

    public static AIRequest ToUnifiedRequest(this JsonElement request, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (request.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completions request JSON must be an object.", nameof(request));

        var model = request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;

        var messages = request.TryGetProperty("messages", out var msgEl) && msgEl.ValueKind == JsonValueKind.Array
            ? ParseRequestMessages(msgEl).ToList()
            : [];

        var tools = request.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
            ? ParseTools(toolsEl).ToList()
            : [];

        var metadata = BuildUnifiedRequestMetadata(request);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = model,
            Input = new AIInput
            {
                Items = messages,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.input.raw_messages"] = msgEl.ValueKind == JsonValueKind.Array ? msgEl.Clone() : null
                }
            },
            Temperature = ExtractValue<float?>(request, "temperature"),
            TopP = ExtractValue<double?>(request, "top_p"),
            MaxOutputTokens = ExtractValue<int?>(request, "max_completion_tokens") ?? ExtractValue<int?>(request, "max_tokens"),
            Stream = ExtractValue<bool?>(request, "stream"),
            ParallelToolCalls = ExtractValue<bool?>(request, "parallel_tool_calls"),
            ToolChoice = request.TryGetProperty("tool_choice", out var toolChoiceEl) ? toolChoiceEl.Clone() : null,
            ResponseFormat = request.TryGetProperty("response_format", out var responseFormatEl) ? responseFormatEl.Clone() : null,
            Tools = tools,
            Metadata = metadata
        };
    }

    public static ChatCompletionOptions ToChatCompletionOptions(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedToolChoice = NormalizeToolChoice(request.ToolChoice, request.Tools);

        var options = new ChatCompletionOptions
        {
            Model = request.Model ?? string.Empty,
            Temperature = request.Temperature,
            ParallelToolCalls = request.ParallelToolCalls,
            Stream = request.Stream,
            Messages = ToChatMessages(request.Input).ToList(),
            Tools = ToChatTools(request.Tools).ToList(),
            ToolChoice = normalizedToolChoice,
            ResponseFormat = request.ResponseFormat,
            Metadata = request.Metadata,
            Store = ExtractMetadataValue<bool?>(request.Metadata, "chatcompletions.request.store")
        };

        return options;
    }

    public static JsonElement ToChatCompletionRequestJson(this AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = new JsonObject();

        var raw = ExtractMetadataElement(request.Metadata, "chatcompletions.request.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
            root = JsonNode.Parse(raw.Value.GetRawText())?.AsObject() ?? new JsonObject();

        var unmapped = ExtractMetadataElement(request.Metadata, "chatcompletions.request.unmapped");
        if (unmapped is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in unmapped.Value.EnumerateObject())
                root[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
            root["model"] = request.Model;

        Set(root, "temperature", request.Temperature);
        Set(root, "top_p", request.TopP);
        Set(root, "max_completion_tokens", request.MaxOutputTokens);
        Set(root, "stream", request.Stream);
        Set(root, "parallel_tool_calls", request.ParallelToolCalls);

        if (request.ToolChoice is not null)
            root["tool_choice"] = ToJsonNode(request.ToolChoice);

        if (request.ResponseFormat is not null)
            root["response_format"] = ToJsonNode(request.ResponseFormat);

        if (request.Tools is { Count: > 0 })
        {
            root["tools"] = JsonValue.Create(JsonSerializer.Serialize(request.Tools.Select(ToRawChatTool).ToList(), Json));
            root["tools"] = ToJsonNode(request.Tools.Select(ToRawChatTool).ToList());
        }

        var messages = ToChatMessages(request.Input).ToList();
        if (messages.Count > 0)
            root["messages"] = ToJsonNode(messages);

        var store = ExtractMetadataValue<bool?>(request.Metadata, "chatcompletions.request.store");
        if (store is not null)
            root["store"] = store.Value;

        return JsonSerializer.SerializeToElement(root, Json);
    }

    public static AIResponse ToUnifiedResponse(this ChatCompletion response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(response, Json);
        return raw.ToUnifiedResponse(providerId);
    }

    public static AIResponse ToUnifiedResponse(this JsonElement response, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (response.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completion response JSON must be an object.", nameof(response));

        var model = ExtractValue<string>(response, "model");
        var outputItems = ParseResponseOutputItems(response).ToList();
        AppendProviderSourceOutputItems(response, outputItems);
        var outputMetadata = BuildUnifiedOutputMetadata(response);

        return new AIResponse
        {
            ProviderId = providerId,
            Model = model,
            Status = InferStatus(response),
            Usage = response.TryGetProperty("usage", out var usage) ? usage.Clone() : null,
            Output = outputItems.Count > 0 || outputMetadata.Count > 0
                ? new AIOutput
                {
                    Items = outputItems.Count > 0 ? outputItems : null,
                    Metadata = outputMetadata.Count > 0 ? outputMetadata : null
                }
                : null,
            Metadata = BuildUnifiedResponseMetadata(response)
        };
    }

    public static ChatCompletion ToChatCompletion(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata;

        var id = ExtractMetadataValue<string>(metadata, "chatcompletions.response.id") ?? $"chatcmpl_{Guid.NewGuid():N}";
        var obj = ExtractMetadataValue<string>(metadata, "chatcompletions.response.object") ?? "chat.completion";
        var created = ExtractMetadataValue<long?>(metadata, "chatcompletions.response.created")
                      ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var model = response.Model
                    ?? ExtractMetadataValue<string>(metadata, "chatcompletions.response.model")
                    ?? "unknown";

        var choices = ExtractMetadataEnumerable(metadata, "chatcompletions.response.choices");
        if (choices.Count == 0)
            choices = BuildChoicesFromOutput(response.Output);

        return new ChatCompletion
        {
            Id = id,
            Object = obj,
            Created = created,
            Model = model,
            Choices = choices,
            Usage = response.Usage,
            AdditionalProperties = BuildChatCompletionAdditionalProperties(metadata)
        };
    }

    public static AIStreamEvent ToUnifiedStreamEvent(this ChatCompletionUpdate update, string providerId)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(update, Json);
        return raw.ToUnifiedStreamEvent(providerId);
    }

    public static IEnumerable<AIStreamEvent> ToUnifiedStreamEvents(
        this ChatCompletionUpdate update,
        string providerId,
        ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState? state = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var chunk = JsonSerializer.SerializeToElement(update, Json);
        if (IsHeartbeatChunk(chunk))
            yield break;

        var metadata = BuildUnifiedStreamMetadata(chunk);
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(
            ExtractValue<long?>(chunk, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        if (state is not null)
        {
            foreach (var toolEvent in MapToolCallEvents(chunk, providerId, timestamp, metadata, state))
                yield return toolEvent;
        }

        var mappedEnvelope = TryMapUiEnvelope(chunk);
        if (mappedEnvelope is not null)
        {
            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = mappedEnvelope,
                Metadata = metadata
            };
        }

        if (mappedEnvelope is null && !HasToolCallDelta(chunk))
            yield return chunk.ToUnifiedStreamEvent(providerId);

        foreach (var sourceEvent in MapSourceUrlEvents(chunk, providerId, timestamp, metadata, state))
            yield return sourceEvent;

        // Always keep raw visibility for debugging and non-lossy pass-through.
        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "chat.completion.chunk",
                Id = ExtractValue<string>(chunk, "id"),
                Timestamp = timestamp,
                Data = chunk.Clone()
            },
            Metadata = metadata
        };
    }

    public static IEnumerable<AIStreamEvent> FinalizeUnifiedStreamEvents(
        string providerId,
        ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState? state,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (state is null || state.PendingToolCalls.Count == 0)
            yield break;

        var ts = timestamp ?? DateTimeOffset.UtcNow;
        foreach (var evt in EmitPendingToolInputs(providerId, ts, new Dictionary<string, object?>(), state))
            yield return evt;
    }

    public static AIStreamEvent ToUnifiedStreamEvent(this JsonElement update, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (update.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completion stream chunk JSON must be an object.", nameof(update));

        var envelope = TryMapUiEnvelope(update)
            ?? new AIEventEnvelope
            {
                Type = ExtractValue<string>(update, "object") ?? "chat.completion.chunk",
                Id = ExtractValue<string>(update, "id"),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                    ExtractValue<long?>(update, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                Data = update.Clone(),
                Output = ParseChunkOutput(update)
            };

        return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = envelope,
            Metadata = BuildUnifiedStreamMetadata(update)
        };
    }

    public static ChatCompletionUpdate ToChatCompletionUpdate(this AIStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        var data = streamEvent.Event.Data;
        if (data is JsonElement chunkEl && chunkEl.ValueKind == JsonValueKind.Object)
        {
            return new ChatCompletionUpdate
            {
                Id = ExtractValue<string>(chunkEl, "id") ?? $"chatcmpl_{Guid.NewGuid():N}",
                Object = ExtractValue<string>(chunkEl, "object") ?? "chat.completion.chunk",
                Created = ExtractValue<long?>(chunkEl, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = ExtractValue<string>(chunkEl, "model") ?? "unknown",
                ServiceTier = ExtractValue<string>(chunkEl, "service_tier"),
                Choices = ExtractEnumerable(chunkEl, "choices"),
                Usage = chunkEl.TryGetProperty("usage", out var usageEl) ? usageEl.Clone() : null,
                AdditionalProperties = ExtractAdditionalProperties(chunkEl, KnownChatCompletionStreamFields)
            };
        }

        var metadata = streamEvent.Metadata;
        return new ChatCompletionUpdate
        {
            Id = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.id") ?? $"chatcmpl_{Guid.NewGuid():N}",
            Object = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.object") ?? "chat.completion.chunk",
            Created = ExtractMetadataValue<long?>(metadata, "chatcompletions.stream.created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.model") ?? "unknown",
            ServiceTier = ExtractMetadataValue<string>(metadata, "chatcompletions.stream.service_tier"),
            Choices = new List<object>(),
            Usage = null,
            AdditionalProperties = BuildChatCompletionUpdateAdditionalProperties(metadata)
        };
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(JsonElement request)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.request.raw"] = request.Clone()
        };

        foreach (var prop in request.EnumerateObject())
            metadata[$"chatcompletions.request.{prop.Name}"] = prop.Value.Clone();

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in request.EnumerateObject())
        {
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["chatcompletions.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(JsonElement response)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.response.raw"] = response.Clone()
        };

        foreach (var prop in response.EnumerateObject())
            metadata[$"chatcompletions.response.{prop.Name}"] = prop.Value.Clone();

        if (response.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.response.provider.search_results"] = searchResults.Clone();

        if (response.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.response.provider.citations"] = citations.Clone();

        if (response.TryGetProperty("images", out var images))
            metadata["chatcompletions.response.provider.images"] = images.Clone();

        if (response.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.response.provider.related_questions"] = relatedQuestions.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedStreamMetadata(JsonElement chunk)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.stream.raw"] = chunk.Clone()
        };

        foreach (var prop in chunk.EnumerateObject())
            metadata[$"chatcompletions.stream.{prop.Name}"] = prop.Value.Clone();

        if (chunk.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.stream.provider.search_results"] = searchResults.Clone();

        if (chunk.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.stream.provider.citations"] = citations.Clone();

        if (chunk.TryGetProperty("images", out var images))
            metadata["chatcompletions.stream.provider.images"] = images.Clone();

        if (chunk.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.stream.provider.related_questions"] = relatedQuestions.Clone();

        return metadata;
    }

    private static Dictionary<string, object?> BuildUnifiedOutputMetadata(JsonElement response)
    {
        var metadata = new Dictionary<string, object?>();

        if (response.TryGetProperty("search_results", out var searchResults))
            metadata["chatcompletions.output.provider.search_results"] = searchResults.Clone();

        if (response.TryGetProperty("citations", out var citations))
            metadata["chatcompletions.output.provider.citations"] = citations.Clone();

        if (response.TryGetProperty("images", out var images))
            metadata["chatcompletions.output.provider.images"] = images.Clone();

        if (response.TryGetProperty("related_questions", out var relatedQuestions))
            metadata["chatcompletions.output.provider.related_questions"] = relatedQuestions.Clone();

        return metadata;
    }

    private static void AppendProviderSourceOutputItems(JsonElement response, List<AIOutputItem> outputItems)
    {
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in outputItems)
        {
            if (item.Metadata is null)
                continue;

            if (ExtractMetadataValue<string>(item.Metadata, "chatcompletions.source.url") is { Length: > 0 } url)
                seenUrls.Add(url);
        }

        if (response.TryGetProperty("search_results", out var searchResults)
            && searchResults.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in searchResults.EnumerateArray())
            {
                if (!TryBuildSourceOutputItem(result, "search_results", seenUrls, out var outputItem))
                    continue;

                outputItems.Add(outputItem!);
            }
        }

        if (response.TryGetProperty("citations", out var citations)
            && citations.ValueKind == JsonValueKind.Array)
        {
            foreach (var citation in citations.EnumerateArray())
            {
                if (!TryBuildSourceOutputItem(citation, "citations", seenUrls, out var outputItem))
                    continue;

                outputItems.Add(outputItem!);
            }
        }
    }

    private static bool TryBuildSourceOutputItem(
        JsonElement source,
        string sourceType,
        HashSet<string> seenUrls,
        out AIOutputItem? item)
    {
        item = null;

        string? url;
        string? title = null;
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
        else
        {
            url = null;
        }

        if (string.IsNullOrWhiteSpace(url) || !seenUrls.Add(url))
            return false;

        item = new AIOutputItem
        {
            Type = "source-url",
            Content = [
                new AITextContentPart
                {
                    Type = "text",
                    Text = title ?? url,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["chatcompletions.source.url"] = url,
                        ["chatcompletions.source.title"] = title,
                        ["chatcompletions.source.type"] = sourceType,
                        ["chatcompletions.source.raw"] = source.Clone()
                    }
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = url,
                ["chatcompletions.source.title"] = title,
                ["chatcompletions.source.source_type"] = sourceType,
                ["chatcompletions.source.date"] = date,
                ["chatcompletions.source.last_updated"] = lastUpdated,
                ["chatcompletions.source.snippet"] = snippet,
                ["chatcompletions.source.origin"] = origin,
                ["chatcompletions.source.raw"] = source.Clone()
            }
        };

        return true;
    }

    private static IEnumerable<AIStreamEvent> MapSourceUrlEvents(
        JsonElement chunk,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState? state)
    {
        if (!chunk.TryGetProperty("search_results", out var searchResults)
            || searchResults.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var source in searchResults.EnumerateArray())
        {
            var url = ExtractValue<string>(source, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            if (state is not null && !state.SeenSourceUrls.Add(url))
                continue;

            var title = ExtractValue<string>(source, "title") ?? url;
            var data = new Dictionary<string, object?>
            {
                ["sourceId"] = url,
                ["url"] = url,
                ["title"] = title,
                ["type"] = "url_citation",
                ["providerMetadata"] = new Dictionary<string, object?>
                {
                    [providerId] = new Dictionary<string, object?>
                    {
                        ["date"] = ExtractValue<string>(source, "date"),
                        ["lastUpdated"] = ExtractValue<string>(source, "last_updated"),
                        ["snippet"] = ExtractValue<string>(source, "snippet"),
                        ["source"] = ExtractValue<string>(source, "source")
                    }
                }
            };

            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = new AIEventEnvelope
                {
                    Type = "source-url",
                    Id = ExtractValue<string>(chunk, "id"),
                    Timestamp = timestamp,
                    Data = data
                },
                Metadata = metadata
            };
        }
    }

    private static IEnumerable<AIInputItem> ParseRequestMessages(JsonElement messages)
    {
        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
                continue;

            var role = ExtractValue<string>(message, "role");
            var content = message.TryGetProperty("content", out var contentEl)
                ? ParseContentParts(contentEl)
                : [];

            var itemMetadata = new Dictionary<string, object?>
            {
                ["chatcompletions.message.raw"] = message.Clone()
            };

            foreach (var prop in message.EnumerateObject())
            {
                if (prop.Name is "role" or "content")
                    continue;

                itemMetadata[$"chatcompletions.message.{prop.Name}"] = prop.Value.Clone();
            }

            yield return new AIInputItem
            {
                Type = "message",
                Role = role,
                Content = [.. content],
                Metadata = itemMetadata
            };
        }
    }

    private static IEnumerable<AIContentPart> ParseContentParts(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = content.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        yield return new AITextContentPart
                        {
                            Text = text,
                            Type = "text",
                            Metadata = new Dictionary<string, object?>
                            {
                                ["chatcompletions.part.raw"] = content.Clone()
                            }
                        };
                    }

                    yield break;
                }
            case JsonValueKind.Array:
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.Object
                        && part.TryGetProperty("type", out var typeEl)
                        && typeEl.ValueKind == JsonValueKind.String)
                    {
                        var type = typeEl.GetString();
                        if (type == "text" && part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        {
                            yield return new AITextContentPart
                            {
                                Type = "text",
                                Text = textEl.GetString() ?? string.Empty,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["chatcompletions.part.raw"] = part.Clone()
                                }
                            };
                            continue;
                        }

                        yield return new AIFileContentPart
                        {
                            MediaType = "application/json",
                            Data = part.Clone(),
                            Type = "file",
                            Metadata = new Dictionary<string, object?>
                            {
                                ["chatcompletions.part.type"] = type,
                                ["chatcompletions.part.raw"] = part.Clone()
                            }
                        };
                        continue;
                    }

                    yield return new AIFileContentPart
                    {
                        MediaType = "application/json",
                        Type = "file",
                        Data = part.Clone(),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["chatcompletions.part.raw"] = part.Clone()
                        }
                    };
                }

                yield break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                yield break;
            default:
                yield return new AIFileContentPart
                {
                    MediaType = "application/json",
                    Data = content.Clone(),
                    Type = "file",
                    Metadata = new Dictionary<string, object?>
                    {
                        ["chatcompletions.part.raw"] = content.Clone()
                    }
                };
                yield break;
        }
    }

    private static IEnumerable<AIToolDefinition> ParseTools(JsonElement tools)
    {
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
                continue;

            var type = ExtractValue<string>(tool, "type") ?? "function";
            var function = tool.TryGetProperty("function", out var fn) ? fn : default;
            var custom = tool.TryGetProperty("custom", out var customEl) ? customEl : default;
            var source = function.ValueKind == JsonValueKind.Object ? function : custom;

            var name = ExtractValue<string>(source, "name") ?? "tool";

            yield return new AIToolDefinition
            {
                Name = name,
                Description = ExtractValue<string>(source, "description"),
                InputSchema = source.TryGetProperty("parameters", out var parameters) ? parameters.Clone() : null,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.tool.raw"] = tool.Clone(),
                    ["chatcompletions.tool.type"] = type,
                    ["chatcompletions.tool.function"] = function.ValueKind == JsonValueKind.Object ? function.Clone() : null,
                    ["chatcompletions.tool.custom"] = custom.ValueKind == JsonValueKind.Object ? custom.Clone() : null
                }
            };
        }
    }

    private static IEnumerable<ChatMessage> ToChatMessages(AIInput? input)
    {
        if (input?.Items is null)
            return Enumerable.Empty<ChatMessage>();

        var list = new List<ChatMessage>();

        foreach (var item in input.Items)
        {
            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(item.Role))
                continue;

            var role = item.Role ?? "user";
            var content = ToChatMessageContent(item.Content, role);

            var toolCallId = ExtractMetadataValue<string>(item.Metadata, "chatcompletions.message.tool_call_id");

            var rawToolCalls = ExtractMetadataElement(item.Metadata, "chatcompletions.message.tool_calls");
            var toolCalls = rawToolCalls is { ValueKind: JsonValueKind.Array }
                ? rawToolCalls.Value.EnumerateArray().Select(e => (object)e.Clone()).ToList()
                : null;

            list.Add(new ChatMessage
            {
                Role = role,
                Content = content,
                ToolCallId = toolCallId,
                ToolCalls = toolCalls
            });
        }

        return list;
    }

    private static JsonElement ToChatMessageContent(IEnumerable<AIContentPart>? parts, string role)
    {
        var list = (parts ?? []).ToList();
        if (list.Count == 0)
            return JsonSerializer.SerializeToElement(string.Empty, Json);

        if (list.Count == 1 && list[0] is AITextContentPart textOnly)
            return JsonSerializer.SerializeToElement(textOnly.Text, Json);

        var mapped = new List<object>();

        foreach (var part in list)
        {
            /*if (part.Metadata is not null && part.Metadata.TryGetValue("chatcompletions.part.raw", out var rawPart) && rawPart is not null)
            {
                mapped.Add(rawPart);
                continue;
            }*/

            if (part is AITextContentPart text)
            {
                mapped.Add(new { type = "text", text = text.Text });
                continue;
            }

            if (part is AIFileContentPart file)
            {
                if (role == "user")
                {
                    if (file.MediaType?.StartsWith("image/") == true)
                    {
                        mapped.Add(new
                        {
                            type = "image_url",
                            image_url = new
                            {
                                url = file.Data
                            }
                        });
                    }
                    else if (file.MediaType?.StartsWith("audio/") == true)
                    {
                        var format = !string.IsNullOrEmpty(file.Filename)
                                    ? Path.GetExtension(file.Filename).TrimStart('.')
                                    : file.MediaType.Split("/").Last() == "mpeg"
                                    ? "mp3" : file.MediaType.Split("/").Last();

                        mapped.Add(new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                format = format,
                                data = file.Data
                            }
                        });
                    }
                    else
                    {
                        mapped.Add(new
                        {
                            type = "file",
                            file = new
                            {
                                filename = file.Filename,
                                file_data = file.Data
                            }
                        });
                    }

                }
            }
        }

        return JsonSerializer.SerializeToElement(mapped, Json);
    }

    private static IEnumerable<object> ToChatTools(List<AIToolDefinition>? tools)
    {
        if (tools is null)
            return Enumerable.Empty<object>();

        return tools.Select(ToRawChatTool).ToList();
    }

    private static object ToRawChatTool(AIToolDefinition tool)
    {
        if (tool.Metadata is not null
            && tool.Metadata.TryGetValue("chatcompletions.tool.raw", out var raw)
            && raw is not null)
            return raw;

        return new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = tool.Description,
                parameters = tool.InputSchema
            }
        };
    }

    private static IEnumerable<AIOutputItem> ParseResponseOutputItems(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return Enumerable.Empty<AIOutputItem>();

        var list = new List<AIOutputItem>();
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            var message = choice.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.Object
                ? messageEl
                : default;

            var role = ExtractValue<string>(message, "role") ?? "assistant";
            var content = message.TryGetProperty("content", out var contentEl)
                ? ParseContentParts(contentEl).ToList()
                : [];

            var metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.choice.raw"] = choice.Clone(),
                ["chatcompletions.choice.index"] = ExtractValue<int?>(choice, "index"),
                ["chatcompletions.choice.finish_reason"] = ExtractValue<string>(choice, "finish_reason"),
                ["chatcompletions.choice.logprobs"] = choice.TryGetProperty("logprobs", out var logprobs) ? logprobs.Clone() : null
            };

            if (message.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in message.EnumerateObject())
                {
                    if (prop.Name is "role" or "content")
                        continue;

                    metadata[$"chatcompletions.message.{prop.Name}"] = prop.Value.Clone();
                }
            }

            list.Add(new AIOutputItem
            {
                Type = "message",
                Role = role,
                Content = content,
                Metadata = metadata
            });
        }

        return list;
    }

    private static List<object> BuildChoicesFromOutput(AIOutput? output)
    {
        var items = output?.Items ?? [];
        var choices = new List<object>();

        var index = 0;
        foreach (var item in items)
        {
            if (!string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = ToChatMessageContent(item.Content, item.Role ?? "assistant");
            var message = new Dictionary<string, object?>
            {
                ["role"] = item.Role ?? "assistant",
                ["content"] = content
            };

            if (item.Metadata is not null)
            {
                foreach (var (key, value) in item.Metadata)
                {
                    if (!key.StartsWith("chatcompletions.message.", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var name = key["chatcompletions.message.".Length..];
                    if (name.Length == 0)
                        continue;

                    message[name] = value;
                }
            }

            var choice = new Dictionary<string, object?>
            {
                ["index"] = index++,
                ["message"] = message,
                ["finish_reason"] = item.Metadata is null
                    ? null
                    : ExtractMetadataValue<string>(item.Metadata, "chatcompletions.choice.finish_reason")
            };

            choices.Add(choice);
        }

        return choices;
    }

    private static AIOutput? ParseChunkOutput(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        var items = new List<AIOutputItem>();

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            var contentParts = new List<AIContentPart>();

            if (delta.TryGetProperty("content", out var contentEl))
            {
                contentParts.AddRange(ParseContentParts(contentEl));
            }

            var role = ExtractValue<string>(delta, "role") ?? "assistant";
            var metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.delta.raw"] = delta.Clone(),
                ["chatcompletions.choice.raw"] = choice.Clone(),
                ["chatcompletions.choice.index"] = ExtractValue<int?>(choice, "index"),
                ["chatcompletions.choice.finish_reason"] = ExtractValue<string>(choice, "finish_reason")
            };

            items.Add(new AIOutputItem
            {
                Type = "message.delta",
                Role = role,
                Content = contentParts,
                Metadata = metadata
            });
        }

        return items.Count > 0 ? new AIOutput { Items = items } : null;
    }

    private static string? InferStatus(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return "completed";

        var first = choices.EnumerateArray().FirstOrDefault();
        var finishReason = ExtractValue<string>(first, "finish_reason");

        return finishReason is null
            ? "in_progress"
            : finishReason.Equals("content_filter", StringComparison.OrdinalIgnoreCase)
                ? "filtered"
                : "completed";
    }

    private static AIEventEnvelope? TryMapUiEnvelope(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("role", out var roleEl)
                    && roleEl.ValueKind == JsonValueKind.String
                    && string.Equals(roleEl.GetString(), "assistant", StringComparison.OrdinalIgnoreCase)
                    && !delta.TryGetProperty("content", out _)
                    && !delta.TryGetProperty("reasoning", out _))
                {
                    return CreateUiEnvelope(chunk, "text-start", string.Empty);
                }

                if (delta.TryGetProperty("content", out var contentEl))
                {
                    var textDelta = contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : ChatMessageContentExtensions.ToText(contentEl);

                    if (!string.IsNullOrEmpty(textDelta))
                        return CreateUiEnvelope(chunk, "text-delta", textDelta);
                }

                if (delta.TryGetProperty("reasoning", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
                {
                    return CreateUiEnvelope(chunk, "reasoning-delta", reasoningEl.GetString() ?? string.Empty);
                }

                if (delta.TryGetProperty("tool_calls", out _))
                    return null;
            }

            var finishReason = ExtractValue<string>(choice, "finish_reason");
            if (!string.IsNullOrWhiteSpace(finishReason))
            {
                var usage = chunk.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
                    ? usageEl
                    : default;

                var data = new Dictionary<string, object?>
                {
                    ["finishReason"] = finishReason == "tool_calls"
                        || finishReason == "function_call" ? "tool-calls" :
                        finishReason == "content_filter" ? "content-filter" : finishReason,
                    ["model"] = ExtractValue<string>(chunk, "model"),
                    ["completed_at"] = ExtractValue<long?>(chunk, "created")
                };

                if (usage.ValueKind == JsonValueKind.Object)
                {
                    data["inputTokens"] = ExtractValue<int?>(usage, "prompt_tokens");
                    data["outputTokens"] = ExtractValue<int?>(usage, "completion_tokens");
                    data["totalTokens"] = ExtractValue<int?>(usage, "total_tokens");
                }

                return CreateUiEnvelope(chunk, "finish", data);
            }
        }

        return null;
    }

    private static AIEventEnvelope CreateUiEnvelope(JsonElement chunk, string type, object data)
    {
        return new AIEventEnvelope
        {
            Type = type,
            Id = ExtractValue<string>(chunk, "id"),
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(
                ExtractValue<long?>(chunk, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            Data = data,
            Output = ParseChunkOutput(chunk),
            Metadata = new Dictionary<string, object?>
            {
                //  ["chatcompletions.stream.raw"] = chunk.Clone(),
                // ["chatcompletions.stream.object"] = ExtractValue<string>(chunk, "object")
            }
        };
    }

    private static bool IsHeartbeatChunk(JsonElement chunk)
    {
        var id = ExtractValue<string>(chunk, "id");
        var model = ExtractValue<string>(chunk, "model");
        var created = ExtractValue<long?>(chunk, "created") ?? 0;
        var hasUsage = chunk.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null;

        var hasChoices = chunk.TryGetProperty("choices", out var choices)
                         && choices.ValueKind == JsonValueKind.Array
                         && choices.EnumerateArray().Any();

        return string.IsNullOrWhiteSpace(id)
               && string.IsNullOrWhiteSpace(model)
               && created <= 0
               && !hasUsage
               && !hasChoices;
    }

    private static bool HasToolCallDelta(JsonElement chunk)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                return true;
        }

        return false;
    }

    private static IEnumerable<AIStreamEvent> MapToolCallEvents(
        JsonElement chunk,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState state)
    {
        if (!chunk.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object)
                continue;

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("tool_calls", out var toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    if (toolCall.ValueKind != JsonValueKind.Object)
                        continue;

                    var index = ExtractValue<int?>(toolCall, "index") ?? 0;
                    if (!state.PendingToolCalls.TryGetValue(index, out var acc))
                    {
                        acc = new ToolCallAccumulator();
                        state.PendingToolCalls[index] = acc;
                    }

                    acc.Id ??= ExtractValue<string>(toolCall, "id") ?? $"call_{Guid.NewGuid():N}";
                    acc.Type ??= ExtractValue<string>(toolCall, "type") ?? "function";

                    if (toolCall.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
                    {
                        acc.Name ??= ExtractValue<string>(functionEl, "name") ?? "unknown_tool";
                        var argsDelta = ExtractValue<string>(functionEl, "arguments");

                        if (!acc.Started)
                        {
                            acc.Started = true;
                            yield return CreateToolInputStartEvent(providerId, acc, timestamp, metadata);
                        }

                        if (!string.IsNullOrEmpty(argsDelta))
                        {
                            acc.Arguments += argsDelta;
                            yield return CreateToolInputDeltaEvent(providerId, acc, argsDelta, timestamp, metadata);
                        }
                    }
                    else if (toolCall.TryGetProperty("custom", out var customEl) && customEl.ValueKind == JsonValueKind.Object)
                    {
                        acc.Name ??= ExtractValue<string>(customEl, "name") ?? "custom_tool";
                        var inputDelta = ExtractValue<string>(customEl, "input");

                        if (!acc.Started)
                        {
                            acc.Started = true;
                            yield return CreateToolInputStartEvent(providerId, acc, timestamp, metadata);
                        }

                        if (!string.IsNullOrEmpty(inputDelta))
                        {
                            acc.Arguments += inputDelta;
                            yield return CreateToolInputDeltaEvent(providerId, acc, inputDelta, timestamp, metadata);
                        }
                    }
                }
            }

            var finishReason = ExtractValue<string>(choice, "finish_reason");
            if (string.Equals(finishReason, "tool_calls", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var completed in EmitPendingToolInputs(providerId, timestamp, metadata, state))
                    yield return completed;
            }
        }
    }

    private static IEnumerable<AIStreamEvent> EmitPendingToolInputs(
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState state)
    {
        foreach (var kvp in state.PendingToolCalls.OrderBy(a => a.Key))
        {
            var acc = kvp.Value;
            if (acc.EmittedAvailable)
                continue;

            acc.EmittedAvailable = true;

            yield return new AIStreamEvent
            {
                ProviderId = providerId,
                Event = new AIEventEnvelope
                {
                    Type = "tool-input-available",
                    Id = acc.Id,
                    Timestamp = timestamp,
                    Data = new Dictionary<string, object?>
                    {
                        ["providerExecuted"] = false,
                        ["toolName"] = acc.Name ?? "unknown_tool",
                        ["input"] = ParseToolInput(acc.Arguments)
                    }
                },
                Metadata = metadata
            };
        }

        state.PendingToolCalls.Clear();
    }

    private static AIStreamEvent CreateToolInputStartEvent(
        string providerId,
        ToolCallAccumulator acc,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "tool-input-start",
                Id = acc.Id,
                Timestamp = timestamp,
                Data = new Dictionary<string, object?>
                {
                    ["providerExecuted"] = false,
                    ["toolName"] = acc.Name ?? "unknown_tool"
                }
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateToolInputDeltaEvent(
        string providerId,
        ToolCallAccumulator acc,
        string delta,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "tool-input-delta",
                Id = acc.Id,
                Timestamp = timestamp,
                Data = delta
            },
            Metadata = metadata
        };

    private static object ParseToolInput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new { };

        try
        {
            return JsonSerializer.Deserialize<object>(raw, Json) ?? new { };
        }
        catch
        {
            return new Dictionary<string, object?> { ["raw"] = raw };
        }
    }

    private static T? ExtractValue<T>(JsonElement obj, string propertyName)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var value))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(value.GetRawText(), Json);
        }
        catch
        {
            return default;
        }
    }

    private static string? TryGetString(object? value)
    {
        if (value is null)
            return null;

        if (value is string s)
            return s;

        if (value is JsonElement j && j.ValueKind == JsonValueKind.String)
            return j.GetString();

        return null;
    }

    private static string? NormalizeToolChoice(object? toolChoice, List<AIToolDefinition>? tools)
    {
        var value = TryGetString(toolChoice)?.Trim();

        if (string.IsNullOrWhiteSpace(value))
            return tools is { Count: > 0 } ? "auto" : "none";

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return "none";

        if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "auto";

        if (value.Equals("required", StringComparison.OrdinalIgnoreCase))
            return "required";

        return tools is { Count: > 0 } ? "auto" : "none";
    }

    private static JsonElement? ExtractMetadataElement(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || value is null)
            return null;

        if (value is JsonElement element)
            return element;

        try
        {
            return JsonSerializer.SerializeToElement(value, Json);
        }
        catch
        {
            return null;
        }
    }

    private static T? ExtractMetadataValue<T>(Dictionary<string, object?>? metadata, string key)
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

    private static List<object> ExtractMetadataEnumerable(Dictionary<string, object?>? metadata, string key)
    {
        var value = ExtractMetadataElement(metadata, key);
        if (value is null || value.Value.ValueKind != JsonValueKind.Array)
            return [];

        return value.Value.EnumerateArray().Select(a => (object)a.Clone()).ToList();
    }

    private static List<object> ExtractEnumerable(JsonElement obj, string propertyName)
    {
        if (!obj.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray().Select(a => (object)a.Clone()).ToList();
    }

    private static void Set<T>(JsonObject obj, string name, T? value)
    {
        if (value is null)
            return;

        obj[name] = JsonValue.Create(value);
    }

    private static JsonNode? ToJsonNode(object value)
    {
        if (value is JsonElement element)
            return JsonNode.Parse(element.GetRawText());

        return JsonSerializer.SerializeToNode(value, Json);
    }

    private static Dictionary<string, JsonElement>? BuildChatCompletionAdditionalProperties(
        Dictionary<string, object?>? metadata)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var known = KnownChatCompletionResponseFields;

        var raw = ExtractMetadataElement(metadata, "chatcompletions.response.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in raw.Value.EnumerateObject())
            {
                if (!known.Contains(prop.Name))
                    additional[prop.Name] = prop.Value.Clone();
            }
        }

        return additional.Count > 0 ? additional : null;
    }

    private static Dictionary<string, JsonElement>? BuildChatCompletionUpdateAdditionalProperties(
        Dictionary<string, object?>? metadata)
    {
        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var known = KnownChatCompletionStreamFields;

        var raw = ExtractMetadataElement(metadata, "chatcompletions.stream.raw");
        if (raw is { ValueKind: JsonValueKind.Object })
        {
            foreach (var prop in raw.Value.EnumerateObject())
            {
                if (!known.Contains(prop.Name))
                    additional[prop.Name] = prop.Value.Clone();
            }
        }

        return additional.Count > 0 ? additional : null;
    }

    private static Dictionary<string, JsonElement>? ExtractAdditionalProperties(JsonElement obj, HashSet<string> knownFields)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        var additional = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            if (!knownFields.Contains(prop.Name))
                additional[prop.Name] = prop.Value.Clone();
        }

        return additional.Count > 0 ? additional : null;
    }

    private static readonly HashSet<string> KnownChatCompletionResponseFields =
    [
        "id",
        "object",
        "created",
        "model",
        "choices",
        "usage"
    ];

    private static readonly HashSet<string> KnownChatCompletionStreamFields =
    [
        "id",
        "object",
        "created",
        "model",
        "service_tier",
        "choices",
        "usage"
    ];

    internal sealed class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public bool Started { get; set; }
        public bool EmittedAvailable { get; set; }
        public string Arguments { get; set; } = string.Empty;
    }
    public sealed class ChatCompletionsStreamMappingState
    {
        internal Dictionary<int, ToolCallAccumulator> PendingToolCalls { get; } = new();

        internal HashSet<string> SeenSourceUrls { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
