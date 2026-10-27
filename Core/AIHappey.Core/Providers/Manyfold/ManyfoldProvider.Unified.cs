using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Diagnostics;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Manyfold;

public partial class ManyfoldProvider
{
    private const string SessionToolName = "manyfold_session";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = AgentId(request.Model);
        var payload = BuildPayload(request, agent, false);
        using var httpRequest = CreateRequest(HttpMethod.Post, "api/v1/chat/completions");
        httpRequest.Content = JsonContent.Create(payload, options: Json);
        var operationId = await EmitRequestDebugAsync(httpRequest, cancellationToken);
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        await EmitResponseDebugAsync(response, responseBody, operationId, cancellationToken);
        EnsureSuccess(response, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var raw = document.RootElement.Clone();
        ThrowIfError(raw);
        var mapped = raw.ToUnifiedResponse(GetIdentifier());
        var session = Text(Property(raw, "metadata"), "session_id") ?? HeaderSession(response)
            ?? Text(Property(payload, "metadata"), "session_id");
        var metadata = ResponseMetadata(raw, agent, session);
        var items = mapped.Output?.Items ?? [];
        var choices = JsonNode.Parse(raw.GetRawText())?["choices"] as JsonArray;
        ConvertToolTranscripts(items, choices);
        if (session is not null)
        {
            var tool = SessionPart(agent, session);
            if (items.Count == 0)
                items.Add(new AIOutputItem { Role = "assistant", Content = [tool] });
            else
            {
                var first = items[0];
                items[0] = new AIOutputItem
                {
                    Type = first.Type, Role = first.Role, Metadata = first.Metadata,
                    Content = [tool, .. first.Content ?? []]
                };
            }

            // Preserve the original Chat Completions shape (including unknown fields) while
            // also returning the synthetic receipt through its normal tool_calls representation.
            if (choices is { Count: > 0 } && choices[0]?["message"] is JsonObject message)
            {
                var calls = message["tool_calls"] as JsonArray ?? new JsonArray();
                if (message["tool_calls"] is null) message["tool_calls"] = calls;
                calls.Add(NativeToolCall(tool));
            }
        }
        if (choices is not null)
            metadata["chatcompletions.response.choices"] = JsonSerializer.SerializeToElement(choices, Json);
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = $"manyfold/{agent}", Status = mapped.Status,
            Usage = Property(raw, "usage") is { ValueKind: JsonValueKind.Object } usage ? usage : mapped.Usage,
            Output = new AIOutput { Items = items, Metadata = metadata }, Metadata = metadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = AgentId(request.Model);
        var payload = BuildPayload(request, agent, true);
        using var httpRequest = CreateRequest(HttpMethod.Post, "api/v1/chat/completions");
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");
        httpRequest.Content = JsonContent.Create(payload, options: Json);
        var operationId = await EmitRequestDebugAsync(httpRequest, cancellationToken);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            await EmitResponseDebugAsync(response, errorBody, operationId, cancellationToken);
            EnsureSuccess(response, errorBody);
        }
        var session = HeaderSession(response) ?? Text(Property(payload, "metadata"), "session_id");
        string? emittedSession = null;
        var id = request.Id ?? $"manyfold-{Guid.NewGuid():N}";
        var textId = id + "-text";
        var reasoningId = id + "-reasoning";
        var transcript = new ToolTranscript();
        var reasoningStarted = false;
        string? finishReason = null;
        var done = false;
        JsonElement? usage = null;
        JsonElement? last = null;
        var providerMetadata = new JsonObject();
        var mapping = new ChatCompletionsUnifiedMapper.ChatCompletionsStreamMappingState();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var observed = _debug.Enabled
            ? new DebugResponseStream(stream, _debug, GetIdentifier(), "api/v1/chat/completions", operationId,
                response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Text.EventStream)
            : null;
        using var reader = new StreamReader(observed ?? stream);
        await foreach (var data in ReadSseAsync(reader, cancellationToken))
        {
            if (data == "[DONE]") { done = true; break; }
            using var document = JsonDocument.Parse(data);
            var raw = document.RootElement.Clone();
            last = raw;
            ThrowIfError(raw);
            id = Text(raw, "id") ?? id;
            if (Property(raw, "usage") is { ValueKind: JsonValueKind.Object } chunkUsage) usage = chunkUsage;
            if (Property(raw, "metadata") is { ValueKind: JsonValueKind.Object } chunkMetadata)
                foreach (var property in chunkMetadata.EnumerateObject())
                    providerMetadata[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            session = Text(Property(raw, "metadata"), "session_id") ?? session;
            if (session is not null && session != emittedSession)
            {
                foreach (var evt in SessionEvents(agent, session)) yield return evt;
                emittedSession = session;
            }

            var update = raw.Deserialize<ChatCompletionUpdate>(Json)!;
            var metadata = ResponseMetadata(raw, agent, session);
            foreach (var mapped in update.ToUnifiedStreamEvents(GetIdentifier(), mapping))
            {
                if (mapped.Event.Data is AIFinishEventData finish)
                {
                    finishReason = finish.FinishReason;
                    continue; // Usage and session metadata may arrive after the finish_reason.
                }
                if (mapped.Event.Data is AITextDeltaEventData text)
                {
                    foreach (var evt in TranscriptEvents(transcript, text.Delta, textId, metadata)) yield return evt;
                    continue;
                }
                if (mapped.Event.Type == "reasoning-delta" && !reasoningStarted)
                {
                    reasoningStarted = true;
                    yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData(), metadata);
                }
                var eventId = mapped.Event.Type == "text-delta" ? textId
                    : mapped.Event.Type == "reasoning-delta" ? reasoningId : mapped.Event.Id;
                yield return Event(mapped.Event.Type, eventId, mapped.Event.Data!, metadata);
            }
            // Keep metadata-only frames visible rather than dropping new provider fields.
            if (Property(raw, "metadata") is not null)
                yield return Event("data-manyfold-metadata", id,
                    new AIDataEventData { Id = id, Data = raw, Transient = false }, metadata);
        }
        if (session is not null && session != emittedSession)
            foreach (var evt in SessionEvents(agent, session)) yield return evt;
        foreach (var evt in ChatCompletionsUnifiedMapper.FinalizeUnifiedStreamEvents(GetIdentifier(), mapping))
            yield return evt;
        var terminal = JsonSerializer.SerializeToElement(new { metadata = providerMetadata, usage, session_id = session, raw = last }, Json);
        var terminalMetadata = ResponseMetadata(terminal, agent, session);
        if (reasoningStarted) yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData(), terminalMetadata);
        foreach (var evt in TranscriptEvents(transcript, "", textId, terminalMetadata, final: true)) yield return evt;
        if (transcript.TextActive) yield return Event("text-end", transcript.TextId, new AITextEndEventData(), terminalMetadata);
        if (!done && finishReason is null)
            throw new IOException("Manyfold stream ended before a finish reason or [DONE].");
        yield return Event("finish", id, new AIFinishEventData
        {
            Model = $"manyfold/{agent}", FinishReason = finishReason ?? "stop", Response = terminal,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            InputTokens = Number(usage, "prompt_tokens"), OutputTokens = Number(usage, "completion_tokens"),
            TotalTokens = Number(usage, "total_tokens"),
            MessageMetadata = AIFinishMessageMetadata.Create($"manyfold/{agent}", DateTimeOffset.UtcNow, usage,
                additionalProperties: new Dictionary<string, object?> { ["manyfold"] = terminal })
        }, terminalMetadata);
    }

    private static JsonObject BuildPayload(AIRequest request, string agent, bool stream)
    {
        var payload = JsonSerializer.SerializeToNode(request.ToChatCompletionOptions("manyfold"), Json)!.AsObject();
        var options = ProviderOptions(request);
        foreach (var property in options) payload[property.Key] = property.Value?.DeepClone();
        foreach (var name in new[] { "store", "headers", "providerMetadata", "providerOptions", "session_id", "sessionId" })
            payload.Remove(name);
        payload["model"] = agent;
        payload["stream"] = stream;
        if (stream)
        {
            var streamOptions = payload["stream_options"] as JsonObject ?? new JsonObject();
            streamOptions["include_usage"] ??= true;
            payload["stream_options"] = streamOptions.DeepClone();
        }
        else payload.Remove("stream_options");

        // Native Chat Completions metadata is provider data, not gateway metadata.
        if (payload["metadata"] is null && request.Metadata?.TryGetValue("chatcompletions.request.metadata", out var native) == true)
            payload["metadata"] = JsonSerializer.SerializeToNode(native, Json);
        var session = Text(options, "session_id") ?? Text(options, "sessionId")
            ?? Text(Property(options, "metadata"), "session_id")
            ?? Text(Property(payload, "metadata"), "session_id") ?? FindSession(request, agent);
        if (session is not null)
        {
            var metadata = payload["metadata"] as JsonObject ?? new JsonObject();
            metadata["session_id"] = session;
            payload["metadata"] = metadata.DeepClone();
        }

        if (payload["messages"] is JsonArray messages)
        {
            var receiptIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in messages.OfType<JsonObject>())
                if (message["tool_calls"] is JsonArray calls)
                    foreach (var call in calls.OfType<JsonObject>().ToList())
                        if (Text(call["function"], "name") == SessionToolName)
                        {
                            if (Text(call, "id") is { } callId) receiptIds.Add(callId);
                            calls.Remove(call);
                        }
            foreach (var message in messages.OfType<JsonObject>().ToList())
            {
                if (Text(message, "role") == "tool" && (receiptIds.Contains(Text(message, "tool_call_id") ?? "")
                    || (Text(message, "tool_call_id")?.StartsWith("manyfold-session-", StringComparison.Ordinal) ?? false)))
                    messages.Remove(message);
                else if (message["tool_calls"] is JsonArray { Count: 0 }) message.Remove("tool_calls");
                if (message.Parent is not null && Text(message, "role") == "assistant"
                    && message["tool_calls"] is null && (message["content"] is null || message["content"] is JsonArray { Count: 0 }))
                    messages.Remove(message);
            }
        }
        return payload;
    }

    private static JsonObject ProviderOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("manyfold", out var value) == true && value is not null)
            return JsonSerializer.SerializeToNode(value, Json) as JsonObject ?? new JsonObject();
        return new JsonObject();
    }

    private static string? FindSession(AIRequest request, string agent)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                // Protocol mappers can omit provider_executed on replay; the reserved tool
                // name and agent-bound receipt identify our continuation, never another tool.
                if (tool.ToolName != SessionToolName
                    && !(tool.ToolCallId?.StartsWith("manyfold-session-", StringComparison.Ordinal) ?? false)) continue;
                foreach (var value in new[] { tool.Output, tool.Input, tool.Metadata })
                    if (ExtractSession(JsonSerializer.SerializeToElement(value, Json), agent) is { } session) return session;
            }
        return null;
    }

    private static string? ExtractSession(JsonElement value, string agent, int depth = 0)
    {
        if (depth > 12) return null;
        if (value.ValueKind == JsonValueKind.String)
        {
            try { using var doc = JsonDocument.Parse(value.GetString()!); return ExtractSession(doc.RootElement, agent, depth + 1); }
            catch (JsonException) { return null; }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (ExtractSession(item, agent, depth + 1) is { } session) return session;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;
        var receiptAgent = Text(value, "agent_id");
        if (receiptAgent is not null && receiptAgent != agent) return null;
        if (receiptAgent == agent && Text(value, "session_id") is { } id) return id;
        foreach (var name in new[] { "structuredContent", "output", "manyfold", "content", "text", "metadata" })
            if (Property(value, name) is { } nested && ExtractSession(nested, agent, depth + 1) is { } session) return session;
        return null;
    }

    private static AIToolCallContentPart SessionPart(string agent, string session) => new()
    {
        Type = "tool-call", ToolCallId = $"manyfold-session-{session}", ToolName = SessionToolName,
        Title = "Manyfold session", ProviderExecuted = true, State = "output-available",
        Input = new { agent_id = agent },
        Output = new { content = Array.Empty<object>(), structuredContent = new { type = SessionToolName, agent_id = agent, session_id = session } },
        Metadata = new Dictionary<string, object?> { ["manyfold"] = new { agent_id = agent, session_id = session } }
    };

    private IEnumerable<AIStreamEvent> SessionEvents(string agent, string session)
    {
        var part = SessionPart(agent, session);
        var metadata = new Dictionary<string, Dictionary<string, object>>
        {
            ["manyfold"] = new() { ["agent_id"] = agent, ["session_id"] = session }
        };
        yield return Event("tool-input-available", part.ToolCallId, new AIToolInputAvailableEventData
        {
            ToolName = SessionToolName, Title = part.Title, Input = part.Input, ProviderExecuted = true, ProviderMetadata = metadata
        });
        yield return Event("tool-output-available", part.ToolCallId, new AIToolOutputAvailableEventData
        {
            ToolName = SessionToolName, Output = part.Output, ProviderExecuted = true, ProviderMetadata = metadata
        });
    }

    private static Dictionary<string, object?> ResponseMetadata(JsonElement raw, string agent, string? session)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["manyfold"] = new { agent_id = agent, session_id = session, raw },
            ["chatcompletions.response.raw"] = raw
        };
        foreach (var property in raw.EnumerateObject())
            metadata[$"chatcompletions.response.{property.Name}"] = property.Value.Clone();
        return metadata;
    }

    private AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?>? metadata = null) => new()
    {
        ProviderId = GetIdentifier(), Metadata = metadata,
        Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow }
    };

    private static ChatCompletionUpdate ToManyfoldChatCompletionUpdate(AIStreamEvent evt, string model)
    {
        var update = evt.ToChatCompletionUpdate();
        object? delta = evt.Event.Data switch
        {
            AITextDeltaEventData text => new { content = text.Delta },
            AIReasoningDeltaEventData reasoning => new { reasoning_content = reasoning.Delta },
            _ => null
        };
        return new ChatCompletionUpdate
        {
            Id = update.Id, Object = update.Object, Created = update.Created, Model = model,
            // Metadata frames retain the native chunk for diagnostics, but its content
            // has already been emitted above as normalized text and executed tools.
            Choices = evt.Event.Type == "data-manyfold-metadata" ? []
                : delta is null ? update.Choices : [new { index = 0, delta, finish_reason = (string?)null }],
            Usage = evt.Event.Data is AIFinishEventData finish ? finish.MessageMetadata?.Usage : null,
            AdditionalProperties = update.AdditionalProperties
        };
    }

    private static string AgentId(string? model)
    {
        var agent = model?.StartsWith("manyfold/", StringComparison.OrdinalIgnoreCase) == true ? model[9..] : model;
        if (string.IsNullOrWhiteSpace(agent)) throw new ArgumentException("Manyfold requires an agent ID as its model.", nameof(model));
        return agent;
    }

    private async Task<string> EmitRequestDebugAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!_debug.Enabled) return string.Empty;
        var operationId = Guid.NewGuid().ToString("n");
        if (request.Content is not null)
            await _debug.EmitAsync(GetIdentifier(), "api/v1/chat/completions", operationId, "request-body",
                ProviderDebugPayload.FromText(await request.Content.ReadAsStringAsync(ct),
                    request.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json), ct);
        return operationId;
    }

    private ValueTask EmitResponseDebugAsync(HttpResponseMessage response, string body, string operationId, CancellationToken ct)
        => _debug.Enabled
            ? _debug.EmitAsync(GetIdentifier(), "api/v1/chat/completions", operationId, "response-body",
                ProviderDebugPayload.FromText(body, response.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json), ct)
            : ValueTask.CompletedTask;

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Manyfold error ({(int)response.StatusCode}): {body}", null, response.StatusCode);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            EnsureSuccess(response, await response.Content.ReadAsStringAsync(ct));
    }

    private static void ThrowIfError(JsonElement raw)
    {
        if (Property(raw, "error") is { ValueKind: not JsonValueKind.Null } error)
            throw new InvalidOperationException($"Manyfold error: {Text(error, "message") ?? error.ToString()}");
    }

    private static string? HeaderSession(HttpResponseMessage response)
        => response.Headers.TryGetValues("x-session-id", out var values) ? values.FirstOrDefault() : null;
    private static JsonElement? Property(JsonElement? value, string name)
        => value is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty(name, out var property) ? property.Clone() : null;
    private static JsonElement? Property(JsonNode? value, string name)
        => value is JsonObject obj && obj[name] is { } node ? JsonSerializer.SerializeToElement(node, Json) : null;
    private static string? Text(JsonElement? value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.String } text && !string.IsNullOrWhiteSpace(text.GetString()) ? text.GetString() : null;
    private static string? Text(JsonNode? value, string name) => Text(value is null ? null : JsonSerializer.SerializeToElement(value, Json), name);
    private static int? Number(JsonElement? value, string name)
        => Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var result) ? result : null;

    private static async IAsyncEnumerable<string> ReadSseAsync(StreamReader reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart(' '));
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}
