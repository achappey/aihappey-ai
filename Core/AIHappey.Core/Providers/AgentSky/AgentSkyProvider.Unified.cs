using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.AgentSky;

public partial class AgentSkyProvider
{
    private const string AgentSkySessionsEndpoint = "v1/sessions";
    private const int AgentSkyEventsPageSize = 500;
    private static readonly JsonSerializerOptions AgentSkyJson = JsonSerializerOptions.Web;

    private sealed record AgentSkySessionResolution(string Id, bool Created, JsonElement? Session);

    private sealed class AgentSkyTurnState
    {
        public HashSet<string> SeenEventIds { get; } = new(StringComparer.Ordinal);
        public bool Terminal { get; set; }
        public string Status { get; set; } = "in_progress";
        public string FinishReason { get; set; } = "stop";
        public JsonElement? TerminalEvent { get; set; }
        public JsonElement? ErrorEvent { get; set; }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        var output = new List<AIOutputItem>();
        string status = "completed";
        Dictionary<string, object?>? responseMetadata = null;

        await foreach (var streamEvent in StreamUnifiedAsync(request, cancellationToken))
        {
            responseMetadata = streamEvent.Metadata ?? responseMetadata;
            var envelope = streamEvent.Event;
            switch (envelope.Type)
            {
                case "text-delta" when envelope.Data is AITextDeltaEventData text:
                    AddOrAppendText(output, text.Delta, false, envelope.Id, envelope.Metadata);
                    break;
                case "reasoning-delta" when envelope.Data is AIReasoningDeltaEventData reasoning:
                    AddOrAppendText(output, reasoning.Delta, true, envelope.Id, envelope.Metadata);
                    break;
                case "tool-input-available" when envelope.Data is AIToolInputAvailableEventData toolInput:
                    AddOrUpdateTool(output, envelope.Id, toolInput.ToolName, toolInput.Title,
                        toolInput.Input, null, "input-available", envelope.Metadata);
                    break;
                case "tool-output-available" when envelope.Data is AIToolOutputAvailableEventData toolOutput:
                    AddOrUpdateTool(output, envelope.Id, toolOutput.ToolName, null,
                        null, toolOutput.Output, "output-available", envelope.Metadata);
                    break;
                case "tool-output-error" when envelope.Data is AIToolOutputErrorEventData toolError:
                    AddOrUpdateTool(output, envelope.Id, null, null,
                        null, toolError.ErrorText, "output-error", envelope.Metadata);
                    status = "failed";
                    break;
                case "file" when envelope.Data is AIFileEventData file:
                    output.Add(new AIOutputItem
                    {
                        Role = "assistant",
                        Content = [new AIFileContentPart
                        {
                            Type = "file",
                            MediaType = file.MediaType,
                            Filename = file.Filename,
                            Data = file.Url,
                            Metadata = envelope.Metadata
                        }]
                    });
                    break;
                case "error":
                    status = "failed";
                    break;
                case "abort":
                    status = "interrupted";
                    break;
                case "finish" when envelope.Data is AIFinishEventData finish:
                    status = finish.FinishReason switch
                    {
                        "error" => "failed",
                        "cancelled" => "interrupted",
                        _ => "completed"
                    };
                    break;
            }
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeAgentSkyModel(request.Model).ToModelId(GetIdentifier()),
            Status = status,
            Output = output.Count == 0 ? null : new AIOutput { Items = output },
            Metadata = responseMetadata
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = NormalizeAgentSkyModel(request.Model);
        if (string.IsNullOrWhiteSpace(agent))
            throw new InvalidOperationException("AgentSky requires an agent slug as the model id.");

        RejectUnsupportedToolContinuations(request);
        var parts = BuildAgentSkyMessageParts(request);
        if (parts.Count == 0)
            throw new InvalidOperationException("AgentSky requires a non-empty latest user message.");

        var session = await ResolveAgentSkySessionAsync(request, agent, cancellationToken);
        var state = new AgentSkyTurnState();
        var model = agent.ToModelId(GetIdentifier());
        var baseline = await ListAllAgentSkyEventsAsync(session.Id, cancellationToken);
        foreach (var item in baseline)
        {
            var id = TryGetString(item, "id");
            if (!string.IsNullOrWhiteSpace(id))
                state.SeenEventIds.Add(id);
        }

        HttpResponseMessage? liveResponse = null;
        var deleteSession = GetOption<bool?>(request, "delete_session") == true;
        var interruptOnCancel = GetOption<bool?>(request, "interrupt_on_cancel") == true;
        try
        {
            liveResponse = await OpenAgentSkyStreamAsync(session.Id, cancellationToken);
            await SendAgentSkyMessageAsync(session.Id, parts, GetOption<string>(request, "idempotency_key"), cancellationToken);

            var reconnects = 0;
            while (!state.Terminal && !cancellationToken.IsCancellationRequested)
            {
                if (liveResponse is not null)
                {
                    await using var liveEvents = ReadAgentSkySseAsync(liveResponse, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                    while (!state.Terminal && !cancellationToken.IsCancellationRequested)
                    {
                        JsonElement providerEvent;
                        bool hasEvent;
                        try
                        {
                            hasEvent = await liveEvents.MoveNextAsync();
                            providerEvent = hasEvent ? liveEvents.Current : default;
                        }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (!hasEvent)
                            break;

                        foreach (var unifiedEvent in MapAgentSkyEvent(providerEvent, session.Id, model, state))
                            yield return unifiedEvent;
                    }

                    liveResponse.Dispose();
                    liveResponse = null;
                }

                if (state.Terminal)
                    break;

                // Open first so events emitted while history is being read are buffered.
                liveResponse = await TryOpenAgentSkyStreamAsync(session.Id, cancellationToken);
                var history = await ListAllAgentSkyEventsAsync(session.Id, cancellationToken);
                foreach (var providerEvent in history)
                {
                    foreach (var unifiedEvent in MapAgentSkyEvent(providerEvent, session.Id, model, state))
                        yield return unifiedEvent;
                }

                if (state.Terminal)
                    break;

                if (liveResponse is null && ++reconnects >= 5)
                    throw new HttpRequestException($"AgentSky stream for session '{session.Id}' disconnected and could not be reopened.");

                if (liveResponse is null)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * reconnects), cancellationToken);
            }

            yield return CreateAgentSkyEvent(
                "finish",
                session.Id,
                new AIFinishEventData
                {
                    FinishReason = state.FinishReason,
                    Model = model,
                    CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(model, DateTimeOffset.UtcNow,
                        additionalProperties: new Dictionary<string, object?>
                        {
                            [GetIdentifier()] = new Dictionary<string, object?>
                            {
                                ["session_id"] = session.Id,
                                ["agent"] = agent,
                                ["terminal_event"] = state.TerminalEvent
                            }
                        })
                },
                DateTimeOffset.UtcNow,
                CreateAgentSkyMetadata(session.Id, agent, state.TerminalEvent));
        }
        finally
        {
            liveResponse?.Dispose();
            if (cancellationToken.IsCancellationRequested && interruptOnCancel)
                await BestEffortAgentSkyLifecycleAsync(session.Id, "interrupt");
            if (deleteSession && session.Created)
                await BestEffortAgentSkyLifecycleAsync(session.Id, null);
        }
    }

    private async Task<AgentSkySessionResolution> ResolveAgentSkySessionAsync(
        AIRequest request, string agent, CancellationToken cancellationToken)
    {
        var existing = GetOption<string>(request, "session_id")
                       ?? GetOption<string>(request, "sessionId");
        if (!string.IsNullOrWhiteSpace(existing))
            return new AgentSkySessionResolution(existing, false, null);

        var body = new Dictionary<string, object?> { ["agent"] = agent };
        CopyOption(request, body, "title", "title");
        CopyOption(request, body, "metadata", "metadata");
        CopyOption(request, body, "eager", "eager");
        CopyOption(request, body, "vcpus", "vcpus");
        CopyOption(request, body, "memoryMb", "memoryMb", "memory_mb");
        CopyOption(request, body, "modelBilling", "modelBilling", "model_billing");

        var instructions = GetOption<object>(request, "instructions");
        if (instructions is not null)
            body["instructions"] = NormalizeAgentSkyInstructions(instructions);
        else if (!string.IsNullOrWhiteSpace(request.Instructions))
            body["instructions"] = new[] { new { name = "instructions.md", content = request.Instructions } };

        var raw = await SendAgentSkyJsonAsync(HttpMethod.Post, AgentSkySessionsEndpoint, body,
            "create session", cancellationToken);
        if (!TryGetProperty(raw, "session", out var session))
            session = raw;
        var id = TryGetString(session, "id")
                 ?? throw new InvalidOperationException("AgentSky create session response did not include an id.");
        return new AgentSkySessionResolution(id, true, session.Clone());
    }

    private async Task SendAgentSkyMessageAsync(string sessionId, List<object> parts,
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{AgentSkySessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/messages")
        {
            Content = JsonContent.Create(new { parts }, options: AgentSkyJson)
        };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureAgentSkySuccessAsync(response, "send message", cancellationToken);
    }

    private async Task<HttpResponseMessage> OpenAgentSkyStreamAsync(string sessionId, CancellationToken cancellationToken)
        => await TryOpenAgentSkyStreamAsync(sessionId, cancellationToken)
           ?? throw new HttpRequestException($"Unable to open AgentSky stream for session '{sessionId}'.");

    private async Task<HttpResponseMessage?> TryOpenAgentSkyStreamAsync(string sessionId, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AgentSkySessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        try
        {
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
                return response;
            response.Dispose();
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async IAsyncEnumerable<JsonElement> ReadAgentSkySseAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new List<string>();
        string? eventName = null;
        string? sseId = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (line.Length == 0)
            {
                if (TryParseAgentSkySse(data, eventName, sseId, out var parsed))
                    yield return parsed;
                data.Clear();
                eventName = null;
                sseId = null;
                continue;
            }
            if (line[0] == ':')
                continue;
            if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Add(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("id:", StringComparison.Ordinal))
                sseId = line[3..].Trim();
        }

        if (TryParseAgentSkySse(data, eventName, sseId, out var final))
            yield return final;
    }

    private static bool TryParseAgentSkySse(List<string> lines, string? eventName, string? sseId,
        out JsonElement result)
    {
        result = default;
        if (lines.Count == 0)
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(string.Join('\n', lines), AgentSkyJson);
            if (parsed.ValueKind != JsonValueKind.Object)
                return false;
            if (string.IsNullOrWhiteSpace(eventName) && string.IsNullOrWhiteSpace(sseId))
            {
                result = parsed.Clone();
                return true;
            }
            var dictionary = parsed.Deserialize<Dictionary<string, object?>>(AgentSkyJson) ?? [];
            if (!dictionary.ContainsKey("type") && !string.IsNullOrWhiteSpace(eventName))
                dictionary["type"] = eventName;
            if (!dictionary.ContainsKey("id") && !string.IsNullOrWhiteSpace(sseId))
                dictionary["id"] = sseId;
            result = JsonSerializer.SerializeToElement(dictionary, AgentSkyJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<List<JsonElement>> ListAllAgentSkyEventsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var uri = $"{AgentSkySessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/events?limit={AgentSkyEventsPageSize}";
            if (!string.IsNullOrWhiteSpace(cursor))
                uri += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await SendAgentSkyJsonAsync(HttpMethod.Get, uri, null, "list events", cancellationToken);
            if (TryGetProperty(page, "events", out var events) && events.ValueKind == JsonValueKind.Array)
                result.AddRange(events.EnumerateArray().Select(item => item.Clone()));
            var hasMore = TryGetBoolean(page, "hasMore") == true;
            cursor = TryGetString(page, "cursor");
            if (!hasMore || string.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor))
                break;
        } while (true);
        return result;
    }

    private IEnumerable<AIStreamEvent> MapAgentSkyEvent(JsonElement providerEvent, string sessionId,
        string model, AgentSkyTurnState state)
    {
        var eventId = TryGetString(providerEvent, "id") ?? $"agentsky-{Guid.NewGuid():N}";
        if (!state.SeenEventIds.Add(eventId))
            yield break;
        var type = TryGetString(providerEvent, "type") ?? "unknown";
        var timestamp = TryGetTimestamp(providerEvent, "at") ?? DateTimeOffset.UtcNow;
        var metadata = CreateAgentSkyMetadata(sessionId, NormalizeAgentSkyModel(model), providerEvent);

        if (type == "agent.message")
        {
            foreach (var partEvent in MapAgentSkyParts(providerEvent, eventId, timestamp, metadata))
                yield return partEvent;
        }
        else if (type == "agent.reasoning")
        {
            var text = ExtractAgentSkyPartText(providerEvent, "reasoning") ?? TryGetString(providerEvent, "text");
            if (!string.IsNullOrEmpty(text))
                foreach (var item in CreateTextSequence(true, eventId, text, timestamp, metadata)) yield return item;
        }
        else if (type is "agent.tool_use" or "agent.tool_result")
        {
            var part = TryGetProperty(providerEvent, "part", out var p) ? p : providerEvent;
            foreach (var item in MapAgentSkyToolPart(part, eventId, type == "agent.tool_result", timestamp, metadata))
                yield return item;
        }
        else if (type == "agent.status")
        {
            yield return CreateAgentSkyEvent("data-agentsky-status", eventId,
                new AIDataEventData { Id = eventId, Data = providerEvent.Clone(), Transient = true },
                timestamp, metadata);
        }
        else if (type == "error")
        {
            state.Status = "failed";
            state.FinishReason = "error";
            state.ErrorEvent = providerEvent.Clone();
            state.Terminal = true;
            state.TerminalEvent = providerEvent.Clone();
            yield return CreateAgentSkyEvent("error", eventId,
                new AIErrorEventData { ErrorText = ExtractAgentSkyError(providerEvent) }, timestamp, metadata);
        }
        else if (type == "turn.interrupted")
        {
            state.Status = "interrupted";
            state.FinishReason = "cancelled";
            state.Terminal = true;
            state.TerminalEvent = providerEvent.Clone();
            yield return CreateAgentSkyEvent("abort", eventId,
                new AIAbortEventData { Reason = "AgentSky turn interrupted." }, timestamp, metadata);
        }
        else if (type == "session.deleted")
        {
            state.Status = "completed";
            state.Terminal = true;
            state.TerminalEvent = providerEvent.Clone();
            yield return CreateAgentSkyEvent("data-agentsky-session-deleted", eventId,
                new AIDataEventData { Id = eventId, Data = providerEvent.Clone() }, timestamp, metadata);
        }
        else if (type == "turn.status_idle" && HasAgentSkyStopReason(providerEvent))
        {
            state.Status = "completed";
            state.Terminal = true;
            state.TerminalEvent = providerEvent.Clone();
            state.FinishReason = string.Equals(GetStopReasonType(providerEvent), "interrupted", StringComparison.OrdinalIgnoreCase)
                ? "cancelled" : "stop";
        }
        else if (type != "user.message")
        {
            yield return CreateAgentSkyEvent("data-agentsky-event", eventId,
                new AIDataEventData { Id = eventId, Data = providerEvent.Clone(), Transient = type.StartsWith("agent.") },
                timestamp, metadata);
        }
    }

    private IEnumerable<AIStreamEvent> MapAgentSkyParts(JsonElement providerEvent, string eventId,
        DateTimeOffset timestamp, Dictionary<string, object?> metadata)
    {
        if (TryGetProperty(providerEvent, "parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var part in parts.EnumerateArray())
            {
                foreach (var item in MapAgentSkyPart(part, $"{eventId}:{index++}", timestamp, metadata))
                    yield return item;
            }
            yield break;
        }

        var text = TryGetString(providerEvent, "text");
        if (!string.IsNullOrEmpty(text))
            foreach (var item in CreateTextSequence(false, eventId, text, timestamp, metadata)) yield return item;
    }

    private IEnumerable<AIStreamEvent> MapAgentSkyPart(JsonElement part, string id,
        DateTimeOffset timestamp, Dictionary<string, object?> metadata)
    {
        var type = TryGetString(part, "type") ?? "text";
        if (type == "text")
        {
            var text = TryGetString(part, "text") ?? string.Empty;
            foreach (var item in CreateTextSequence(false, id, text, timestamp, metadata)) yield return item;
        }
        else if (type == "reasoning")
        {
            var text = TryGetString(part, "text") ?? TryGetString(part, "reasoning") ?? string.Empty;
            foreach (var item in CreateTextSequence(true, id, text, timestamp, metadata)) yield return item;
        }
        else if (type is "tool_call" or "tool_use")
        {
            foreach (var item in MapAgentSkyToolPart(part, id, false, timestamp, metadata)) yield return item;
        }
        else if (type == "tool_result")
        {
            foreach (var item in MapAgentSkyToolPart(part, id, true, timestamp, metadata)) yield return item;
        }
        else if (type is "file" or "image" or "video")
        {
            var url = TryGetString(part, "url") ?? TryGetString(part, "data") ?? TryGetString(part, "content") ?? string.Empty;
            yield return CreateAgentSkyEvent("file", id, new AIFileEventData
            {
                MediaType = TryGetString(part, "mediaType") ?? TryGetString(part, "mimeType")
                            ?? (type == "image" ? "image/*" : type == "video" ? "video/*" : "application/octet-stream"),
                Filename = TryGetString(part, "filename") ?? TryGetString(part, "name"),
                Url = url,
                ProviderMetadata = ProviderMetadata(part)
            }, timestamp, metadata);
        }
        else if (type == "error")
        {
            yield return CreateAgentSkyEvent("error", id,
                new AIErrorEventData { ErrorText = ExtractAgentSkyError(part) }, timestamp, metadata);
        }
        else
        {
            yield return CreateAgentSkyEvent("data-agentsky-part", id,
                new AIDataEventData { Id = id, Data = part.Clone(), Transient = type == "status" }, timestamp, metadata);
        }
    }

    private IEnumerable<AIStreamEvent> MapAgentSkyToolPart(JsonElement part, string fallbackId, bool result,
        DateTimeOffset timestamp, Dictionary<string, object?> metadata)
    {
        var id = TryGetString(part, "id") ?? TryGetString(part, "toolCallId")
                 ?? TryGetString(part, "tool_call_id") ?? fallbackId;
        var name = TryGetString(part, "name") ?? TryGetString(part, "toolName") ?? "agentsky_tool";
        if (!result)
        {
            var input = GetJsonValue(part, "input") ?? GetJsonValue(part, "arguments") ?? new { };
            yield return CreateAgentSkyEvent("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = name,
                Title = TryGetString(part, "title") ?? name,
                Input = input,
                ProviderExecuted = true,
                ProviderMetadata = ProviderMetadata(part)
            }, timestamp, metadata);
        }
        else
        {
            var output = GetJsonValue(part, "output") ?? GetJsonValue(part, "result")
                         ?? GetJsonValue(part, "content") ?? new { };
            var isError = TryGetBoolean(part, "isError") == true || TryGetBoolean(part, "error") == true;
            if (isError)
                yield return CreateAgentSkyEvent("tool-output-error", id, new AIToolOutputErrorEventData
                {
                    ToolCallId = id,
                    ErrorText = output.ToString() ?? "AgentSky tool failed.",
                    ProviderExecuted = true,
                    Dynamic = true,
                    ProviderMetadata = ProviderMetadata(part)
                }, timestamp, metadata);
            else
                yield return CreateAgentSkyEvent("tool-output-available", id, new AIToolOutputAvailableEventData
                {
                    ToolName = name,
                    Output = output,
                    ProviderExecuted = true,
                    Dynamic = true,
                    ProviderMetadata = ProviderMetadata(part)
                }, timestamp, metadata);
        }
    }

    private IEnumerable<AIStreamEvent> CreateTextSequence(bool reasoning, string id, string text,
        DateTimeOffset timestamp, Dictionary<string, object?> metadata)
    {
        var providerMetadata = ProviderMetadata(JsonSerializer.SerializeToElement(metadata, AgentSkyJson));
        if (reasoning)
        {
            yield return CreateAgentSkyEvent("reasoning-start", id,
                new AIReasoningStartEventData { ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield return CreateAgentSkyEvent("reasoning-delta", id,
                new AIReasoningDeltaEventData { Delta = text, ProviderMetadata = providerMetadata }, timestamp, metadata);
            yield return CreateAgentSkyEvent("reasoning-end", id,
                new AIReasoningEndEventData { ProviderMetadata = providerMetadata }, timestamp, metadata);
        }
        else
        {
            var flat = providerMetadata.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
            yield return CreateAgentSkyEvent("text-start", id,
                new AITextStartEventData { ProviderMetadata = flat }, timestamp, metadata);
            yield return CreateAgentSkyEvent("text-delta", id,
                new AITextDeltaEventData { Delta = text, ProviderMetadata = flat }, timestamp, metadata);
            yield return CreateAgentSkyEvent("text-end", id,
                new AITextEndEventData { ProviderMetadata = flat }, timestamp, metadata);
        }
    }

    private static List<object> BuildAgentSkyMessageParts(AIRequest request)
    {
        var latestUser = request.Input?.Items?.LastOrDefault(item =>
            string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (latestUser?.Content is not null)
            return latestUser.Content.Select(ToAgentSkyPart).Where(part => part is not null).Cast<object>().ToList();
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return [new { type = "text", index = 0, text = request.Input.Text }];
        return [];
    }

    private static object? ToAgentSkyPart(AIContentPart part)
        => part switch
        {
            AITextContentPart text => new { type = "text", text = text.Text },
            AIReasoningContentPart reasoning => new { type = "reasoning", text = reasoning.Text },
            AIFileContentPart file when file.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                => new { type = "image", mediaType = file.MediaType, filename = file.Filename, data = file.Data },
            AIFileContentPart file when file.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true
                => new { type = "video", mediaType = file.MediaType, filename = file.Filename, data = file.Data },
            AIFileContentPart file => new { type = "file", mediaType = file.MediaType, filename = file.Filename, data = file.Data },
            _ => null
        };

    private static void RejectUnsupportedToolContinuations(AIRequest request)
    {
        var toolPart = request.Input?.Items?.SelectMany(item => item.Content ?? [])
            .OfType<AIToolCallContentPart>().FirstOrDefault(part => !part.IsProviderToolCall);
        if (toolPart is not null)
            throw new NotSupportedException("AgentSky executes tools inside its agent harness; client-side tool continuations are not supported.");
    }

    private async Task<JsonElement> SendAgentSkyJsonAsync(HttpMethod method, string uri, object? body,
        string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: AgentSkyJson);
        using var response = await _client.SendAsync(request, cancellationToken);
        await EnsureAgentSkySuccessAsync(response, operation, cancellationToken);
        if (response.Content.Headers.ContentLength == 0)
            return JsonSerializer.SerializeToElement(new { }, AgentSkyJson);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return (await JsonSerializer.DeserializeAsync<JsonElement>(stream, AgentSkyJson, cancellationToken)).Clone();
    }

    private static async Task EnsureAgentSkySuccessAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string message = body;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryGetProperty(document.RootElement, "error", out var error))
                message = TryGetString(error, "message") ?? body;
        }
        catch (JsonException) { }
        throw new HttpRequestException($"AgentSky {operation} failed ({(int)response.StatusCode}): {message}",
            null, response.StatusCode);
    }

    private async Task BestEffortAgentSkyLifecycleAsync(string sessionId, string? action)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ApplyAuthHeader();
            var uri = $"{AgentSkySessionsEndpoint}/{Uri.EscapeDataString(sessionId)}";
            using var request = new HttpRequestMessage(action == "interrupt" ? HttpMethod.Post : HttpMethod.Delete,
                action == "interrupt" ? $"{uri}/interrupt" : uri);
            using var response = await _client.SendAsync(request, timeout.Token);
        }
        catch { }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            { value = property.Value; return true; }
        return false;
    }

    private static string? TryGetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool? TryGetBoolean(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static DateTimeOffset? TryGetTimestamp(JsonElement element, string name)
        => DateTimeOffset.TryParse(TryGetString(element, name), out var value) ? value : null;

    private static object? GetJsonValue(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? value.Clone() : null;

    private static string ExtractAgentSkyError(JsonElement element)
        => TryGetString(element, "message") ?? TryGetString(element, "text")
           ?? (TryGetProperty(element, "error", out var error) ? TryGetString(error, "message") : null)
           ?? "AgentSky reported an error.";

    private static string? ExtractAgentSkyPartText(JsonElement element, string expectedType)
    {
        if (!TryGetProperty(element, "part", out var part))
            return null;
        var type = TryGetString(part, "type");
        return string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase)
            ? TryGetString(part, "text") ?? TryGetString(part, expectedType) : null;
    }

    private static bool HasAgentSkyStopReason(JsonElement element)
        => !string.IsNullOrWhiteSpace(GetStopReasonType(element));

    private static string? GetStopReasonType(JsonElement element)
        => TryGetProperty(element, "stop_reason", out var stopReason)
            ? TryGetString(stopReason, "type") : null;

    private Dictionary<string, object?> CreateAgentSkyMetadata(string sessionId, string agent, JsonElement? raw)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["agent"] = NormalizeAgentSkyModel(agent),
                ["raw"] = raw
            }
        };

    private Dictionary<string, Dictionary<string, object>> ProviderMetadata(JsonElement raw)
        => new()
        {
            [GetIdentifier()] = new Dictionary<string, object> { ["raw"] = raw.Clone() }
        };

    private AIStreamEvent CreateAgentSkyEvent(string type, string? id, object data,
        DateTimeOffset timestamp, Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data,
                Metadata = metadata
            },
            Metadata = metadata
        };

    private static string NormalizeAgentSkyModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;
        var slash = model.IndexOf('/');
        return slash >= 0 && string.Equals(model[..slash], "agentsky", StringComparison.OrdinalIgnoreCase)
            ? model[(slash + 1)..] : model;
    }

    private T GetOption<T>(AIRequest request, params string[] names)
    {
        if (request.Metadata is null
            || !request.Metadata.TryGetValue(GetIdentifier(), out var providerOptions)
            || providerOptions is null)
            return default!;

        JsonElement options;
        try
        {
            options = providerOptions is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(providerOptions, AgentSkyJson);
        }
        catch
        {
            return default!;
        }

        foreach (var name in names)
        {
            try
            {
                if (TryGetProperty(options, name, out var raw)
                    && raw.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                    return raw.Deserialize<T>(AgentSkyJson)!;
            }
            catch { }
        }
        return default!;
    }

    private void CopyOption(AIRequest request, Dictionary<string, object?> body, string target, params string[] names)
    {
        var value = GetOption<object>(request, names);
        if (value is not null)
            body[target] = value;
    }

    private static object NormalizeAgentSkyInstructions(object instructions)
    {
        if (instructions is string content)
            return new[] { new { name = "instructions.md", content } };
        return instructions;
    }

    private static void AddOrAppendText(List<AIOutputItem> output, string text, bool reasoning,
        string? id, Dictionary<string, object?>? metadata)
    {
        var last = output.LastOrDefault()?.Content?.LastOrDefault();
        if (!reasoning && last is AITextContentPart previousText)
        {
            output[^1].Content![^1] = new AITextContentPart
            { Type = "text", Text = previousText.Text + text, Metadata = previousText.Metadata };
            return;
        }
        if (reasoning && last is AIReasoningContentPart previousReasoning)
        {
            output[^1].Content![^1] = new AIReasoningContentPart
            { Type = "reasoning", Text = (previousReasoning.Text ?? string.Empty) + text, Metadata = previousReasoning.Metadata };
            return;
        }
        output.Add(new AIOutputItem
        {
            Role = "assistant",
            Content = reasoning
                ? [new AIReasoningContentPart { Type = "reasoning", Text = text, Metadata = metadata }]
                : [new AITextContentPart { Type = "text", Text = text, Metadata = metadata }]
        });
    }

    private static void AddOrUpdateTool(List<AIOutputItem> output, string? id, string? name,
        string? title, object? input, object? result, string state, Dictionary<string, object?>? metadata)
    {
        var toolId = id ?? $"agentsky-tool-{Guid.NewGuid():N}";
        var existingItem = output.FirstOrDefault(item => item.Content?.OfType<AIToolCallContentPart>()
            .Any(tool => tool.ToolCallId == toolId) == true);
        var existing = existingItem?.Content?.OfType<AIToolCallContentPart>().First(tool => tool.ToolCallId == toolId);
        var updated = new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = toolId,
            ToolName = name ?? existing?.ToolName,
            Title = title ?? existing?.Title,
            Input = input ?? existing?.Input,
            Output = result ?? existing?.Output,
            State = state,
            ProviderExecuted = true,
            Metadata = metadata ?? existing?.Metadata
        };
        if (existingItem is null)
            output.Add(new AIOutputItem { Role = "assistant", Content = [updated] });
        else
            existingItem.Content![existingItem.Content.IndexOf(existing!)] = updated;
    }
}
