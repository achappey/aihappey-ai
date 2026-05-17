using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Tavily;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var model = NormalizeResearchModel(request.Model);
        var input = BuildPromptFromUnifiedRequest(request);
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input derived from the unified request.");

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        var providerMetadata = GetUnifiedProviderMetadata(request);

        var queued = await QueueResearchTaskAsync(input, model, stream: false, outputSchema, providerMetadata, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.RequestId, cancellationToken);

        return ToUnifiedResponse(completed, request, model);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var model = NormalizeResearchModel(request.Model);
        var input = BuildPromptFromUnifiedRequest(request);
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input derived from the unified request.");

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        var providerMetadata = GetUnifiedProviderMetadata(request);
        var seenSourceUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emittedToolInputs = new HashSet<string>(StringComparer.Ordinal);
        var emittedToolOutputs = new HashSet<string>(StringComparer.Ordinal);
        var textStarted = false;
        var streamId = request.Id ?? $"tavily_{Guid.NewGuid():N}";
        var latestModel = model;
        string finishReason = "stop";
        JsonElement? lastUsage = null;
        DateTimeOffset lastTimestamp = DateTimeOffset.UtcNow;

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, providerMetadata, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Id) && string.Equals(streamId, request.Id ?? streamId, StringComparison.Ordinal))
                streamId = evt.Id!;

            if (!string.IsNullOrWhiteSpace(evt.Model))
                latestModel = evt.Model!;

            if (!string.IsNullOrWhiteSpace(evt.FinishReason))
                finishReason = evt.FinishReason!;

            if (evt.Usage is JsonElement usageElement)
                lastUsage = usageElement.Clone();

            lastTimestamp = evt.Created > 0
                ? DateTimeOffset.FromUnixTimeSeconds(evt.Created)
                : DateTimeOffset.UtcNow;

            var metadata = BuildStreamMetadata(evt, latestModel);

            foreach (var toolEvent in MapToolEvents(
                         evt,
                         providerId,
                         lastTimestamp,
                         metadata,
                         emittedToolInputs,
                         emittedToolOutputs))
            {
                yield return toolEvent;
            }

            foreach (var source in evt.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url) || !seenSourceUrls.Add(source.Url))
                    continue;

                yield return CreateSourceStreamEvent(providerId, source, lastTimestamp, metadata);
            }

            if (!string.IsNullOrWhiteSpace(evt.ContentText))
            {
                if (!textStarted)
                {
                    textStarted = true;
                    yield return CreateStreamEvent(providerId, "text-start", streamId, lastTimestamp, new AITextStartEventData(), metadata);
                }

                yield return CreateStreamEvent(
                    providerId,
                    "text-delta",
                    streamId,
                    lastTimestamp,
                    new AITextDeltaEventData { Delta = evt.ContentText! },
                    metadata);
            }

            if (evt.ContentObject is JsonElement contentObject)
            {
                yield return CreateStreamEvent(
                    providerId,
                    "data-tavily.structured-output",
                    streamId,
                    lastTimestamp,
                    new AIDataEventData
                    {
                        Id = streamId,
                        Data = ToPlainObject(contentObject) ?? new { }
                    },
                    metadata);
            }
        }

        if (textStarted)
            yield return CreateStreamEvent(providerId, "text-end", streamId, lastTimestamp, new AITextEndEventData(), new Dictionary<string, object?>());

        var usageObject = lastUsage is JsonElement usage
            ? ToPlainObject(usage)
            : new Dictionary<string, object?>
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            };

        var inputTokens = lastUsage is JsonElement inputUsage ? TryGetUsageInt(inputUsage, "input_tokens", "prompt_tokens") : null;
        var outputTokens = lastUsage is JsonElement outputUsage ? TryGetUsageInt(outputUsage, "output_tokens", "completion_tokens") : null;
        var totalTokens = lastUsage is JsonElement totalUsage ? TryGetUsageInt(totalUsage, "total_tokens") : null;

        yield return CreateStreamEvent(
            providerId,
            "finish",
            streamId,
            lastTimestamp,
            new AIFinishEventData
            {
                FinishReason = finishReason,
                Model = latestModel,
                CompletedAt = lastTimestamp.ToUnixTimeSeconds(),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    latestModel,
                    lastTimestamp,
                    usage: usageObject,
                    inputTokens: inputTokens,
                    outputTokens: outputTokens,
                    totalTokens: totalTokens,
                    temperature: request.Temperature)
            },
            new Dictionary<string, object?>());
    }

    private AIResponse ToUnifiedResponse(TavilyCompletedTask completed, AIRequest request, string model)
    {
        var text = ToOutputText(completed.Content);
        var items = new List<AIOutputItem>
        {
            new()
            {
                Type = "message",
                Role = "assistant",
                Content =
                [
                    new AITextContentPart
                    {
                        Type = "text",
                        Text = text,
                        Metadata = completed.Content is null || completed.Content is string
                            ? null
                            : new Dictionary<string, object?>
                            {
                                ["tavily.structured_output"] = completed.Content
                            }
                    }
                ]
            }
        };

        items.AddRange(completed.Sources.Select(CreateSourceOutputItem));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = "completed",
            Usage = new
            {
                response_time = completed.ResponseTime,
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Output = new AIOutput
            {
                Items = items
            },
            Metadata = BuildUnifiedResponseMetadata(request, completed, model)
        };
    }

    private TavilyProviderMetadata? GetUnifiedProviderMetadata(AIRequest request)
        => request.Metadata.GetProviderMetadata<TavilyProviderMetadata>(GetIdentifier());

    private static string NormalizeResearchModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return "auto";

        var trimmed = model.Trim();
        return trimmed.StartsWith("tavily/", StringComparison.OrdinalIgnoreCase)
            ? trimmed.Split('/', 2)[1]
            : trimmed;
    }

    private static string BuildPromptFromUnifiedRequest(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var items = request.Input?.Items?.ToList() ?? [];
        if (items.Count == 0)
            return request.Instructions ?? string.Empty;

        var system = new List<string>();
        var lines = new List<string>();

        foreach (var item in items)
        {
            var role = (item.Role ?? string.Empty).Trim().ToLowerInvariant();
            var text = ExtractUnifiedInputText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (role == "system")
            {
                system.Add(text);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            lines.Add($"{role}: {text}");
        }

        if (system.Count > 0)
            lines.Insert(0, $"system: {string.Join("\n\n", system)}");

        var prompt = string.Join("\n\n", lines);
        return string.IsNullOrWhiteSpace(prompt)
            ? request.Instructions ?? string.Empty
            : prompt;
    }

    private static string ExtractUnifiedInputText(IEnumerable<AIContentPart>? parts)
        => string.Join(
            "\n",
            (parts ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static AIOutputItem CreateSourceOutputItem(TavilySource source)
        => new()
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = source.Title ?? source.Url
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = source.Url,
                ["chatcompletions.source.title"] = source.Title,
                ["messages.source.url"] = source.Url,
                ["messages.source.title"] = source.Title,
                ["tavily.source.favicon"] = source.Favicon
            }
        };

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(AIRequest request, TavilyCompletedTask completed, string model)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["tavily.request_id"] = completed.RequestId,
            ["tavily.created_at"] = completed.CreatedAt,
            ["tavily.created_at_unix"] = ToUnixTime(completed.CreatedAt),
            ["tavily.response_time"] = completed.ResponseTime,
            ["tavily.model"] = model,
            ["tavily.input"] = BuildPromptFromUnifiedRequest(request)
        };

        if (completed.RawResponse is JsonElement raw)
            metadata["tavily.response.raw"] = raw.Clone();

        if (completed.Content is not null && completed.Content is not string)
            metadata["tavily.structured_output"] = completed.Content;

        if (completed.Sources.Count > 0)
            metadata["tavily.sources"] = completed.Sources.Select(ToSourceDto).ToList();

        return metadata;
    }

    private static TavilyCompletedTask ToCompletedTask(AIResponse response)
    {
        var metadata = response.Metadata;
        var structured = TryGetMetadata<object>(metadata, "tavily.structured_output");

        return new TavilyCompletedTask
        {
            RequestId = TryGetMetadata<string>(metadata, "tavily.request_id") ?? Guid.NewGuid().ToString("n"),
            CreatedAt = TryGetMetadata<DateTime?>(metadata, "tavily.created_at")
                ?? (TryGetMetadata<long?>(metadata, "tavily.created_at_unix") is { } createdAtUnix
                    ? DateTimeOffset.FromUnixTimeSeconds(createdAtUnix).UtcDateTime
                    : DateTime.UtcNow),
            Content = structured ?? ExtractOutputText(response.Output),
            Sources = ExtractSources(response.Output),
            ResponseTime = TryGetMetadata<double?>(metadata, "tavily.response_time")
                ?? TryGetUsageValue<double?>(response.Usage, "response_time")
                ?? 0
        };
    }

    private static string ExtractOutputText(AIOutput? output)
        => string.Concat(
            (output?.Items ?? [])
                .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text));

    private static List<TavilySource> ExtractSources(AIOutput? output)
    {
        var sources = new List<TavilySource>();

        foreach (var item in output?.Items ?? [])
        {
            if (!string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = TryGetMetadata<string>(item.Metadata, "chatcompletions.source.url")
                ?? TryGetMetadata<string>(item.Metadata, "messages.source.url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            sources.Add(new TavilySource
            {
                Url = url,
                Title = TryGetMetadata<string>(item.Metadata, "chatcompletions.source.title")
                    ?? TryGetMetadata<string>(item.Metadata, "messages.source.title"),
                Favicon = TryGetMetadata<string>(item.Metadata, "tavily.source.favicon")
            });
        }

        return sources;
    }

    private static bool TryGetStructuredOutputData(AIStreamEvent streamEvent, out object? data)
    {
        if (streamEvent.Event.Type.StartsWith("data-tavily.structured-output", StringComparison.OrdinalIgnoreCase)
            && streamEvent.Event.Data is AIDataEventData dataEvent)
        {
            data = dataEvent.Data;
            return true;
        }

        data = null;
        return false;
    }

    private static IEnumerable<AIStreamEvent> MapToolEvents(
        TavilyStreamEvent evt,
        string providerId,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata,
        HashSet<string> emittedToolInputs,
        HashSet<string> emittedToolOutputs)
    {
        if (evt.Delta is not JsonElement delta
            || !delta.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var type = toolCalls.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        if (string.Equals(type, "tool_call", StringComparison.OrdinalIgnoreCase)
            && toolCalls.TryGetProperty("tool_call", out var toolCallArray)
            && toolCallArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolCall in toolCallArray.EnumerateArray())
            {
                if (toolCall.ValueKind != JsonValueKind.Object)
                    continue;

                var toolCallId = toolCall.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
                    : Guid.NewGuid().ToString("n");

                if (!emittedToolInputs.Add(toolCallId))
                    continue;

                var toolName = toolCall.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? "tool"
                    : "tool";

                var providerMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object?>
                {
                    ["raw"] = toolCall.Clone(),
                    ["parent_tool_call_id"] = toolCall.TryGetProperty("parent_tool_call_id", out var parentIdEl) && parentIdEl.ValueKind == JsonValueKind.String
                        ? parentIdEl.GetString()
                        : null
                });

                yield return new AIStreamEvent
                {
                    ProviderId = providerId,
                    Event = new AIEventEnvelope
                    {
                        Type = "tool-input-start",
                        Id = toolCallId,
                        Timestamp = timestamp,
                        Data = new AIToolInputStartEventData
                        {
                            ToolName = toolName,
                            Title = toolName,
                            ProviderExecuted = true,
                            ProviderMetadata = providerMetadata
                        }
                    },
                    Metadata = metadata
                };

                yield return new AIStreamEvent
                {
                    ProviderId = providerId,
                    Event = new AIEventEnvelope
                    {
                        Type = "tool-input-available",
                        Id = toolCallId,
                        Timestamp = timestamp,
                        Data = new AIToolInputAvailableEventData
                        {
                            ToolName = toolName,
                            Title = toolName,
                            ProviderExecuted = true,
                            Input = BuildToolInputPayload(toolCall),
                            ProviderMetadata = providerMetadata
                        }
                    },
                    Metadata = metadata
                };
            }
        }

        if (string.Equals(type, "tool_response", StringComparison.OrdinalIgnoreCase)
            && toolCalls.TryGetProperty("tool_response", out var toolResponseArray)
            && toolResponseArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var toolResponse in toolResponseArray.EnumerateArray())
            {
                if (toolResponse.ValueKind != JsonValueKind.Object)
                    continue;

                var toolCallId = toolResponse.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
                    : Guid.NewGuid().ToString("n");

                if (!emittedToolOutputs.Add(toolCallId))
                    continue;

                var toolName = toolResponse.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? "tool"
                    : "tool";

                var providerMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object?>
                {
                    ["raw"] = toolResponse.Clone(),
                    ["parent_tool_call_id"] = toolResponse.TryGetProperty("parent_tool_call_id", out var parentIdEl) && parentIdEl.ValueKind == JsonValueKind.String
                        ? parentIdEl.GetString()
                        : null
                });

                yield return new AIStreamEvent
                {
                    ProviderId = providerId,
                    Event = new AIEventEnvelope
                    {
                        Type = "tool-output-available",
                        Id = toolCallId,
                        Timestamp = timestamp,
                        Data = new AIToolOutputAvailableEventData
                        {
                            ToolName = toolName,
                            ProviderExecuted = true,
                            Output = BuildToolOutputPayload(toolResponse),
                            ProviderMetadata = providerMetadata
                        }
                    },
                    Metadata = metadata
                };
            }
        }
    }

    private static object BuildToolInputPayload(JsonElement toolCall)
    {
        var payload = new Dictionary<string, object?>();

        if (toolCall.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.String)
            payload["arguments"] = argumentsEl.GetString();

        if (toolCall.TryGetProperty("queries", out var queriesEl) && queriesEl.ValueKind == JsonValueKind.Array)
            payload["queries"] = ToPlainObject(queriesEl);

        if (toolCall.TryGetProperty("parent_tool_call_id", out var parentIdEl) && parentIdEl.ValueKind == JsonValueKind.String)
            payload["parent_tool_call_id"] = parentIdEl.GetString();

        return payload;
    }

    private static object BuildToolOutputPayload(JsonElement toolResponse)
    {
        var payload = new Dictionary<string, object?>();

        if (toolResponse.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.String)
            payload["arguments"] = argumentsEl.GetString();

        if (toolResponse.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
            payload["sources"] = ToPlainObject(sourcesEl);

        if (toolResponse.TryGetProperty("parent_tool_call_id", out var parentIdEl) && parentIdEl.ValueKind == JsonValueKind.String)
            payload["parent_tool_call_id"] = parentIdEl.GetString();

        return payload;
    }

    private static AIStreamEvent CreateSourceStreamEvent(
        string providerId,
        TavilySource source,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "source-url",
                Id = source.Url,
                Timestamp = timestamp,
                Data = new AISourceUrlEventData
                {
                    SourceId = source.Url,
                    Url = source.Url,
                    Title = source.Title,
                    Type = "url_citation",
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object?>
                    {
                        ["favicon"] = source.Favicon
                    })
                }
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateStreamEvent(
        string providerId,
        string type,
        string? id,
        DateTimeOffset timestamp,
        object data,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static Dictionary<string, object?> BuildStreamMetadata(TavilyStreamEvent evt, string model)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["tavily.stream.id"] = evt.Id,
            ["tavily.stream.model"] = string.IsNullOrWhiteSpace(evt.Model) ? model : evt.Model,
            ["tavily.stream.created"] = evt.Created
        };

        if (evt.Raw is JsonElement raw)
            metadata["tavily.stream.raw"] = raw.Clone();

        if (evt.Delta is JsonElement delta)
            metadata["tavily.stream.delta"] = delta.Clone();

        if (evt.Usage is JsonElement usage)
            metadata["tavily.stream.usage"] = usage.Clone();

        return metadata;
    }

    private static Dictionary<string, Dictionary<string, object>>? CreateScopedProviderMetadata(
        string providerId,
        Dictionary<string, object?> values)
    {
        var filtered = values
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.Ordinal);

        return filtered.Count == 0
            ? null
            : new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal)
            {
                [providerId] = filtered
            };
    }

    private static int? TryGetUsageInt(JsonElement usage, params string[] names)
    {
        foreach (var name in names)
        {
            if (usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetInt32();
        }

        return null;
    }

    private static T? TryGetMetadata<T>(Dictionary<string, object?>? metadata, string key)
    {
        if (metadata?.TryGetValue(key, out var value) != true || value is null)
            return default;

        if (value is T typed)
            return typed;

        try
        {
            if (value is JsonElement json)
                return JsonSerializer.Deserialize<T>(json.GetRawText(), JsonSerializerOptions.Web);

            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), JsonSerializerOptions.Web);
        }
        catch
        {
            return default;
        }
    }

    private static T? TryGetUsageValue<T>(object? usage, string key)
    {
        if (usage is null)
            return default;

        try
        {
            var json = usage is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);

            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(key, out var value))
                return default;

            return JsonSerializer.Deserialize<T>(value.GetRawText(), JsonSerializerOptions.Web);
        }
        catch
        {
            return default;
        }
    }

    private static object? ToPlainObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), JsonSerializerOptions.Web);
}
