using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Linkup;

public partial class LinkupProvider
{
    private const string DefaultLinkupMode = "Auto";
    private const string DefaultLinkupReasoningDepth = "L";
    private static readonly TimeSpan LinkupPollInitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LinkupPollMaxDelay = TimeSpan.FromSeconds(10);

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var query = BuildLinkupResearchQuery(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Linkup research requires non-empty text from the last user message.");

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        var providerMetadata = GetLinkupProviderMetadata(request);
        var payload = CreateLinkupResearchPayload(query, outputSchema, providerMetadata);
        var queued = await QueueLinkupResearchTaskAsync(payload, cancellationToken);
        var completed = await WaitForLinkupResearchCompletionAsync(queued.Id, cancellationToken);

        return ToUnifiedResponse(completed, request, payload);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var providerId = GetIdentifier();
        var eventId = request.Id ?? $"linkup_{Guid.NewGuid():N}";
        var query = BuildLinkupResearchQuery(request);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Linkup research requires non-empty text from the last user message.");

        var outputSchema = TryExtractOutputSchema(request.ResponseFormat);
        var providerMetadata = GetLinkupProviderMetadata(request);
        var payload = CreateLinkupResearchPayload(query, outputSchema, providerMetadata);

        var queued = await QueueLinkupResearchTaskAsync(payload, cancellationToken);

        yield return CreateLinkupStreamEvent(
            providerId,
            "data-linkup-research-task",
            queued.Id,
            new AIDataEventData
            {
                Id = queued.Id,
                Data = ToTaskData(queued),
                Transient = true
            },
            DateTimeOffset.UtcNow,
            BuildQueuedTaskMetadata(queued, payload));

        LinkupResearchTask? completed = null;
        Exception? pollingException = null;
        try
        {
            completed = await WaitForLinkupResearchCompletionAsync(queued.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pollingException = ex;
        }

        if (pollingException is not null)
        {
            yield return CreateLinkupStreamEvent(
                providerId,
                "error",
                queued.Id,
                new AIErrorEventData { ErrorText = pollingException.Message },
                DateTimeOffset.UtcNow,
                BuildQueuedTaskMetadata(queued, payload));

            yield return CreateFinishStreamEvent(
                providerId,
                queued.Id,
                request,
                null,
                payload,
                "error");
            yield break;
        }

        if (completed is null)
            throw new InvalidOperationException($"Linkup research task '{queued.Id}' did not produce a terminal result.");

        var timestamp = ParseDateTimeOffsetOrNow(completed.UpdatedAt);
        var metadata = BuildUnifiedResponseMetadata(request, completed, payload);
        var output = completed.Output;
        var text = ExtractLinkupOutputText(output);

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return CreateLinkupStreamEvent(providerId, "text-start", eventId, new AITextStartEventData(), timestamp, metadata);
            yield return CreateLinkupStreamEvent(
                providerId,
                "text-delta",
                eventId,
                new AITextDeltaEventData { Delta = text },
                timestamp,
                metadata);
            yield return CreateLinkupStreamEvent(providerId, "text-end", eventId, new AITextEndEventData(), timestamp, metadata);
        }

        if (output is JsonElement outputElement && TryGetLinkupSources(outputElement, out var sources))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source.Url) || !seen.Add(source.Url))
                    continue;

                yield return CreateSourceStreamEvent(providerId, source, timestamp, metadata);
            }
        }

        if (output is JsonElement structuredOutput
            && !IsSourcedAnswerOutput(structuredOutput))
        {
            yield return CreateLinkupStreamEvent(
                providerId,
                "data-linkup.structured-output",
                eventId,
                new AIDataEventData
                {
                    Id = eventId,
                    Data = ToPlainObject(structuredOutput) ?? new { }
                },
                timestamp,
                metadata);
        }

        yield return CreateFinishStreamEvent(providerId, eventId, request, completed, payload, ResolveFinishReason(completed.Status));
    }

    private async Task<LinkupResearchTask> QueueLinkupResearchTaskAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/research")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Linkup research queue failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return ToLinkupResearchTask(doc.RootElement);
    }

    private async Task<LinkupResearchTask> WaitForLinkupResearchCompletionAsync(string taskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Linkup research task id is required.", nameof(taskId));

        var delay = LinkupPollInitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"v1/research/{Uri.EscapeDataString(taskId)}");
            using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var text = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Linkup research poll failed ({(int)resp.StatusCode}): {text}");

            using var doc = JsonDocument.Parse(text);
            var task = ToLinkupResearchTask(doc.RootElement);

            if (string.Equals(task.Status, "completed", StringComparison.OrdinalIgnoreCase))
                return task;

            if (string.Equals(task.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                var error = string.IsNullOrWhiteSpace(task.Error) ? "Unknown Linkup research failure." : task.Error;
                throw new InvalidOperationException($"Linkup research task '{taskId}' failed: {error}");
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(LinkupPollMaxDelay.TotalMilliseconds, delay.TotalMilliseconds * 2));
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private AIResponse ToUnifiedResponse(
        LinkupResearchTask completed,
        AIRequest request,
        Dictionary<string, object?> payload)
    {
        var output = completed.Output;
        var text = ExtractLinkupOutputText(output);
        var outputItems = new List<AIOutputItem>();

        outputItems.Add(new AIOutputItem
        {
            Type = "message",
            Role = "assistant",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = output is JsonElement json && !IsSourcedAnswerOutput(json)
                        ? new Dictionary<string, object?>
                        {
                            ["linkup.structured_output"] = json.Clone()
                        }
                        : null
                }
            ]
        });

        if (output is JsonElement outputElement && TryGetLinkupSources(outputElement, out var sources))
            outputItems.AddRange(sources.Select(CreateSourceOutputItem));

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = ResolveUnifiedModel(request),
            Status = ToUnifiedTaskStatus(completed.Status),
            Usage = CreateUsage(completed),
            Output = new AIOutput { Items = outputItems },
            Metadata = BuildUnifiedResponseMetadata(request, completed, payload)
        };
    }

    private static Dictionary<string, object?> CreateLinkupResearchPayload(
        string query,
        object? outputSchema,
        LinkupProviderMetadata? providerMetadata)
    {
        var outputType = !string.IsNullOrWhiteSpace(providerMetadata?.OutputType)
            ? providerMetadata!.OutputType!
            : outputSchema is null ? "sourcedAnswer" : "structured";

        var payload = new Dictionary<string, object?>
        {
            ["q"] = query,
            ["outputType"] = outputType,
            ["mode"] = string.IsNullOrWhiteSpace(providerMetadata?.Mode) ? DefaultLinkupMode : providerMetadata!.Mode,
            ["reasoningDepth"] = string.IsNullOrWhiteSpace(providerMetadata?.ReasoningDepth) ? DefaultLinkupReasoningDepth : providerMetadata!.ReasoningDepth
        };

        if (providerMetadata?.IncludeImages is not null)
            payload["includeImages"] = providerMetadata.IncludeImages;

        if (providerMetadata?.IncludeDomains?.Count > 0)
            payload["includeDomains"] = providerMetadata.IncludeDomains;

        if (providerMetadata?.ExcludeDomains?.Count > 0)
            payload["excludeDomains"] = providerMetadata.ExcludeDomains;

        if (!string.IsNullOrWhiteSpace(providerMetadata?.FromDate))
            payload["fromDate"] = providerMetadata.FromDate;

        if (!string.IsNullOrWhiteSpace(providerMetadata?.ToDate))
            payload["toDate"] = providerMetadata.ToDate;

        if (outputSchema is not null)
            payload["structuredOutputSchema"] = JsonSerializer.Serialize(outputSchema, JsonSerializerOptions.Web);

        if (providerMetadata?.AdditionalProperties is not null)
        {
            foreach (var property in providerMetadata.AdditionalProperties)
            {
                if (property.Key is "q" or "outputType" or "mode" or "reasoningDepth" or "includeImages"
                    or "includeDomains" or "excludeDomains" or "fromDate" or "toDate" or "structuredOutputSchema")
                    continue;

                payload[property.Key] = JsonSerializer.Deserialize<object>(property.Value.GetRawText(), JsonSerializerOptions.Web);
            }
        }

        return payload;
    }

    private LinkupProviderMetadata? GetLinkupProviderMetadata(AIRequest request)
        => request.Metadata.GetProviderMetadata<LinkupProviderMetadata>(GetIdentifier());

    private static string BuildLinkupResearchQuery(AIRequest request)
    {
        var lastUserText = request.Input?.Items?
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => ExtractUnifiedText(item.Content))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

        if (!string.IsNullOrWhiteSpace(lastUserText))
            return lastUserText!;

        return request.Input?.Items?.Count > 0
            ? string.Empty
            : request.Input?.Text ?? string.Empty;
    }

    private static string ExtractUnifiedText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? []).OfType<AITextContentPart>().Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));

    private static object? TryExtractOutputSchema(object? format)
    {
        if (format is null)
            return null;

        var schema = format.GetJSONSchema();
        if (schema?.JsonSchema is not null)
        {
            var element = schema.JsonSchema.Schema;
            if (element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
                return JsonSerializer.Deserialize<object>(element.GetRawText(), JsonSerializerOptions.Web);
        }

        try
        {
            var raw = JsonSerializer.SerializeToElement(format, JsonSerializerOptions.Web);
            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("json_schema", out var jsonSchema)
                && jsonSchema.ValueKind == JsonValueKind.Object
                && jsonSchema.TryGetProperty("schema", out var schemaEl)
                && schemaEl.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(schemaEl.GetRawText(), JsonSerializerOptions.Web);
            }

            if (raw.ValueKind == JsonValueKind.Object
                && raw.TryGetProperty("schema", out var directSchema)
                && directSchema.ValueKind == JsonValueKind.Object)
            {
                return JsonSerializer.Deserialize<object>(directSchema.GetRawText(), JsonSerializerOptions.Web);
            }
        }
        catch
        {
            // ignore schema extraction failures
        }

        return null;
    }

    private static LinkupResearchTask ToLinkupResearchTask(JsonElement root)
    {
        var id = GetString(root, "id") ?? Guid.NewGuid().ToString("n");
        var status = GetString(root, "status") ?? "pending";
        var createdAt = GetString(root, "createdAt");
        var updatedAt = GetString(root, "updatedAt");
        var error = GetString(root, "error");
        JsonElement? output = root.TryGetProperty("output", out var outputEl)
            && outputEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? outputEl.Clone()
            : null;
        JsonElement? input = root.TryGetProperty("input", out var inputEl)
            && inputEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? inputEl.Clone()
            : null;

        return new LinkupResearchTask
        {
            Id = id,
            Type = GetString(root, "type") ?? "research",
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Error = error,
            Input = input,
            Output = output,
            Raw = root.Clone()
        };
    }

    private static string ExtractLinkupOutputText(JsonElement? output)
    {
        if (output is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;

        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? string.Empty;

        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("answer", out var answer)
            && answer.ValueKind == JsonValueKind.String)
        {
            return answer.GetString() ?? string.Empty;
        }

        return element.GetRawText();
    }

    private static bool TryGetLinkupSources(JsonElement output, out List<LinkupSource> sources)
    {
        sources = [];
        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("sources", out var sourcesEl)
            || sourcesEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var sourceEl in sourcesEl.EnumerateArray())
        {
            if (sourceEl.ValueKind != JsonValueKind.Object)
                continue;

            var url = GetString(sourceEl, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            sources.Add(new LinkupSource
            {
                Url = url!,
                Name = GetString(sourceEl, "name"),
                Snippet = GetString(sourceEl, "snippet"),
                Favicon = GetString(sourceEl, "favicon"),
                Raw = sourceEl.Clone()
            });
        }

        return sources.Count > 0;
    }

    private static AIOutputItem CreateSourceOutputItem(LinkupSource source)
        => new()
        {
            Type = "source-url",
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = source.Name ?? source.Url
                }
            ],
            Metadata = new Dictionary<string, object?>
            {
                ["chatcompletions.source.url"] = source.Url,
                ["chatcompletions.source.title"] = source.Name,
                ["messages.source.url"] = source.Url,
                ["messages.source.title"] = source.Name,
                ["linkup.source.snippet"] = source.Snippet,
                ["linkup.source.favicon"] = source.Favicon,
                ["linkup.source.raw"] = source.Raw
            }
        };

    private static AIStreamEvent CreateSourceStreamEvent(
        string providerId,
        LinkupSource source,
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
                    Title = source.Name,
                    Type = "url_citation",
                    ProviderMetadata = CreateScopedProviderMetadata(providerId, new Dictionary<string, object?>
                    {
                        ["snippet"] = source.Snippet,
                        ["favicon"] = source.Favicon,
                        ["raw"] = source.Raw
                    })
                }
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateFinishStreamEvent(
        string providerId,
        string eventId,
        AIRequest request,
        LinkupResearchTask? completed,
        Dictionary<string, object?> payload,
        string finishReason)
    {
        var timestamp = completed is null
            ? DateTimeOffset.UtcNow
            : ParseDateTimeOffsetOrNow(completed.UpdatedAt);
        var usage = completed is null ? CreateUsage(payload) : CreateUsage(completed);
        var metadata = completed is null
            ? new Dictionary<string, object?> { ["linkup.request.payload"] = payload }
            : BuildUnifiedResponseMetadata(request, completed, payload);

        return CreateLinkupStreamEvent(
            providerId,
            "finish",
            eventId,
            new AIFinishEventData
            {
                FinishReason = finishReason,
                Model = ResolveUnifiedModel(request),
                CompletedAt = timestamp.ToUnixTimeSeconds(),
                MessageMetadata = AIFinishMessageMetadata.Create(
                    ResolveUnifiedModel(request),
                    timestamp,
                    usage: usage,
                    inputTokens: 0,
                    outputTokens: 0,
                    totalTokens: 0,
                    temperature: request.Temperature)
            },
            timestamp,
            metadata);
    }

    private static AIStreamEvent CreateLinkupStreamEvent(
        string providerId,
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata = null)
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

    private static Dictionary<string, object?> BuildUnifiedResponseMetadata(
        AIRequest request,
        LinkupResearchTask completed,
        Dictionary<string, object?> payload)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["linkup.task.id"] = completed.Id,
            ["linkup.task.type"] = completed.Type,
            ["linkup.task.status"] = completed.Status,
            ["linkup.task.created_at"] = completed.CreatedAt,
            ["linkup.task.updated_at"] = completed.UpdatedAt,
            ["linkup.task.error"] = completed.Error,
            ["linkup.request.q"] = BuildLinkupResearchQuery(request),
            ["linkup.request.payload"] = payload,
            ["linkup.response.raw"] = completed.Raw
        };

        if (completed.Input is JsonElement input)
            metadata["linkup.task.input"] = input.Clone();

        if (completed.Output is JsonElement output)
        {
            metadata["linkup.task.output"] = output.Clone();

            if (!IsSourcedAnswerOutput(output))
                metadata["linkup.structured_output"] = output.Clone();

            if (TryGetLinkupSources(output, out var sources))
                metadata["linkup.sources"] = sources.Select(ToSourceDto).ToList();
        }

        return metadata;
    }

    private static Dictionary<string, object?> BuildQueuedTaskMetadata(
        LinkupResearchTask queued,
        Dictionary<string, object?> payload)
        => new()
        {
            ["linkup.task.id"] = queued.Id,
            ["linkup.task.status"] = queued.Status,
            ["linkup.task.created_at"] = queued.CreatedAt,
            ["linkup.task.updated_at"] = queued.UpdatedAt,
            ["linkup.request.payload"] = payload,
            ["linkup.response.raw"] = queued.Raw
        };

    private static object CreateUsage(LinkupResearchTask completed)
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["created_at"] = completed.CreatedAt,
            ["updated_at"] = completed.UpdatedAt
        };

    private static object CreateUsage(Dictionary<string, object?> payload)
        => new Dictionary<string, object?>
        {
            ["prompt_tokens"] = 0,
            ["completion_tokens"] = 0,
            ["total_tokens"] = 0,
            ["mode"] = payload.TryGetValue("mode", out var mode) ? mode : null,
            ["reasoningDepth"] = payload.TryGetValue("reasoningDepth", out var depth) ? depth : null
        };

    private static object ToTaskData(LinkupResearchTask task)
        => new Dictionary<string, object?>
        {
            ["id"] = task.Id,
            ["type"] = task.Type,
            ["status"] = task.Status,
            ["createdAt"] = task.CreatedAt,
            ["updatedAt"] = task.UpdatedAt,
            ["error"] = task.Error
        };

    private static object ToSourceDto(LinkupSource source)
        => new
        {
            name = source.Name,
            url = source.Url,
            favicon = source.Favicon,
            snippet = source.Snippet
        };

    private static bool IsSourcedAnswerOutput(JsonElement output)
        => output.ValueKind == JsonValueKind.Object
           && output.TryGetProperty("answer", out _)
           && output.TryGetProperty("sources", out _);

    private static string ResolveFinishReason(string? status)
        => string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop";

    private static string ToUnifiedTaskStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? "failed" : "in_progress";

    private static string ResolveUnifiedModel(AIRequest request)
        => string.IsNullOrWhiteSpace(request.Model) ? "linkup/research" : request.Model!;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => property.ToString()
        };
    }

    private static DateTimeOffset ParseDateTimeOffsetOrNow(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : DateTimeOffset.UtcNow;

    private static object? ToPlainObject(JsonElement element)
        => JsonSerializer.Deserialize<object>(element.GetRawText(), JsonSerializerOptions.Web);

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

    private sealed class LinkupProviderMetadata
    {
        public string? Mode { get; set; }

        public string? ReasoningDepth { get; set; }

        public string? OutputType { get; set; }

        public bool? IncludeImages { get; set; }

        public List<string>? IncludeDomains { get; set; }

        public List<string>? ExcludeDomains { get; set; }

        public string? FromDate { get; set; }

        public string? ToDate { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }

    private sealed class LinkupResearchTask
    {
        public string Id { get; init; } = default!;

        public string Type { get; init; } = "research";

        public string Status { get; init; } = default!;

        public string? CreatedAt { get; init; }

        public string? UpdatedAt { get; init; }

        public string? Error { get; init; }

        public JsonElement? Input { get; init; }

        public JsonElement? Output { get; init; }

        public JsonElement Raw { get; init; }
    }

    private sealed class LinkupSource
    {
        public string Url { get; init; } = default!;

        public string? Name { get; init; }

        public string? Snippet { get; init; }

        public string? Favicon { get; init; }
        public JsonElement Raw { get; init; }
    }
}
