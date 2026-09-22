using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Ellipsis;

public partial class EllipsisProvider
{
    private const int EllipsisStreamProtocol = 6;
    private static readonly TimeSpan EllipsisPollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan EllipsisPollTimeout = TimeSpan.FromMinutes(10);

    private sealed record EllipsisSessionResolution(
        string Id,
        bool Created,
        string Target,
        JsonElement Session,
        string? MessageId);

    private sealed class EllipsisStreamState
    {
        public HashSet<string> SeenRecordIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StartedTextIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EndedTextIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StartedReasoningIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EndedReasoningIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ToolInputs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ToolOutputs { get; } = new(StringComparer.Ordinal);
        public long LastFeedSequence { get; set; }
        public string? TurnId { get; set; }
        public JsonElement? Session { get; set; }
        public JsonElement? LatestExecution { get; set; }
        public bool Terminal { get; set; }
        public bool Failed { get; set; }
        public bool Cancelled { get; set; }
        public string? Error { get; set; }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var output = new List<AIOutputItem>();
        var content = new List<AIContentPart>();
        var text = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var reasoning = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var tools = new Dictionary<string, AIToolCallContentPart>(StringComparer.Ordinal);
        Dictionary<string, object?>? metadata = null;
        object? usage = null;
        var status = "completed";

        await foreach (var streamEvent in StreamUnifiedAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            metadata = streamEvent.Metadata ?? metadata;
            var id = streamEvent.Event.Id ?? Guid.NewGuid().ToString("N");

            switch (streamEvent.Event.Type)
            {
                case "text-start":
                    text.TryAdd(id, new StringBuilder());
                    break;
                case "text-delta" when streamEvent.Event.Data is AITextDeltaEventData delta:
                    if (!text.TryGetValue(id, out var textBuffer))
                        text[id] = textBuffer = new StringBuilder();
                    textBuffer.Append(delta.Delta);
                    break;
                case "reasoning-start":
                    reasoning.TryAdd(id, new StringBuilder());
                    break;
                case "reasoning-delta" when streamEvent.Event.Data is AIReasoningDeltaEventData delta:
                    if (!reasoning.TryGetValue(id, out var reasoningBuffer))
                        reasoning[id] = reasoningBuffer = new StringBuilder();
                    reasoningBuffer.Append(delta.Delta);
                    break;
                case "tool-input-available" when streamEvent.Event.Data is AIToolInputAvailableEventData input:
                    tools[id] = new AIToolCallContentPart
                    {
                        ToolCallId = id,
                        Type = "tool-call",
                        ToolName = input.ToolName,
                        Title = input.Title,
                        Input = input.Input,
                        ProviderExecuted = input.ProviderExecuted,
                        State = "input-available",
                        Metadata = FlattenEllipsisProviderMetadata(input.ProviderMetadata)
                    };
                    break;
                case "tool-output-available" when streamEvent.Event.Data is AIToolOutputAvailableEventData toolOutput:
                    tools.TryGetValue(id, out var existing);
                    tools[id] = new AIToolCallContentPart
                    {
                        ToolCallId = id,
                        Type = "tool-call",
                        ToolName = existing?.ToolName ?? toolOutput.ToolName,
                        Title = existing?.Title,
                        Input = existing?.Input,
                        Output = toolOutput.Output,
                        ProviderExecuted = existing?.ProviderExecuted ?? toolOutput.ProviderExecuted,
                        State = "output-available",
                        Metadata = existing?.Metadata ?? FlattenEllipsisProviderMetadata(toolOutput.ProviderMetadata)
                    };
                    break;
                case "tool-output-error" when streamEvent.Event.Data is AIToolOutputErrorEventData toolError:
                    tools.TryGetValue(id, out existing);
                    tools[id] = new AIToolCallContentPart
                    {
                        ToolCallId = id,
                        Type = "tool-call",
                        ToolName = existing?.ToolName,
                        Title = existing?.Title,
                        Input = existing?.Input,
                        Output = new CallToolResult
                        {
                            IsError = true,
                            StructuredContent = JsonSerializer.SerializeToElement(new { error = toolError.ErrorText }, EllipsisJson)
                        },
                        ProviderExecuted = true,
                        State = "output-error",
                        Metadata = existing?.Metadata
                    };
                    break;
                case "data-ellipsis.structured-output" when streamEvent.Event.Data is AIDataEventData structured:
                    content.Add(new AITextContentPart
                    {
                        Type = "text",
                        Text = structured.Data is JsonElement json ? json.GetRawText() : JsonSerializer.Serialize(structured.Data, EllipsisJson),
                        Metadata = new Dictionary<string, object?> { ["ellipsis.structured_output"] = structured.Data }
                    });
                    break;
                case "error":
                    status = "failed";
                    break;
                case "finish" when streamEvent.Event.Data is AIFinishEventData finish:
                    usage = finish.MessageMetadata?.Usage.Clone();
                    status = finish.FinishReason switch
                    {
                        "error" => "failed",
                        "cancelled" => "cancelled",
                        _ => "completed"
                    };
                    break;
            }
        }

        content.InsertRange(0, tools.Values);
        content.AddRange(reasoning.Values
            .Where(static value => value.Length > 0)
            .Select(static value => (AIContentPart)new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = value.ToString()
            }));
        content.AddRange(text.Values
            .Where(static value => value.Length > 0)
            .Select(static value => (AIContentPart)new AITextContentPart
            {
                Type = "text",
                Text = value.ToString()
            }));

        if (content.Count > 0)
        {
            output.Add(new AIOutputItem
            {
                Type = "message",
                Role = "assistant",
                Content = content,
                Metadata = metadata
            });
        }

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = NormalizeEllipsisModel(request.Model).ToModelId(GetIdentifier()),
            Status = status,
            Usage = usage,
            Metadata = metadata,
            Output = output.Count == 0 ? null : new AIOutput { Items = output, Metadata = metadata }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolution = await ResolveEllipsisSessionAsync(request, cancellationToken);
        var model = NormalizeEllipsisModel(request.Model).ToModelId(GetIdentifier());
        var state = new EllipsisStreamState { Session = resolution.Session };

        if (resolution.Created)
        {
            foreach (var streamEvent in CreateEllipsisSessionToolEvents(resolution, DateTimeOffset.UtcNow))
                yield return streamEvent;
        }

        var webSocketSucceeded = false;
        await using (var webSocketEvents = StreamEllipsisWebSocketAsync(
                         resolution,
                         model,
                         state,
                         cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool hasEvent;
                try
                {
                    hasEvent = await webSocketEvents.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    break;
                }

                if (!hasEvent)
                    break;

                webSocketSucceeded = true;
                yield return webSocketEvents.Current;
            }
        }

        if (!state.Terminal)
        {
            await foreach (var streamEvent in PollEllipsisSessionAsync(resolution, model, state, cancellationToken))
                yield return streamEvent;
        }
        else if (!webSocketSucceeded)
        {
            await foreach (var streamEvent in ReplayEllipsisRecordsAsync(resolution.Id, model, state, cancellationToken))
                yield return streamEvent;
        }

        foreach (var streamEvent in CloseEllipsisOpenParts(state, DateTimeOffset.UtcNow))
            yield return streamEvent;

        await EnrichEllipsisTerminalStateAsync(resolution.Id, state, cancellationToken);
        if (await TryGetEllipsisJsonAsync($"{EllipsisSessionsEndpoint}/{Uri.EscapeDataString(resolution.Id)}/output", cancellationToken) is { } output
            && TryGetEllipsisProperty(output, "output", out var structuredOutput)
            && structuredOutput.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            yield return CreateEllipsisEvent(
                "data-ellipsis.structured-output",
                $"ellipsis-output-{resolution.Id}",
                new AIDataEventData { Id = resolution.Id, Data = structuredOutput.Clone(), Transient = false },
                DateTimeOffset.UtcNow,
                CreateEllipsisResponseMetadata(resolution, state));
        }

        yield return CreateEllipsisFinishEvent(resolution, model, state, DateTimeOffset.UtcNow);
    }

    private async Task<EllipsisSessionResolution> ResolveEllipsisSessionAsync(
        AIRequest request,
        CancellationToken cancellationToken)
    {
        var target = NormalizeEllipsisModel(request.Model);
        var message = ExtractLatestEllipsisUserText(request);

        if (TryFindEllipsisSessionId(request, out var existingSessionId))
        {
            var sent = await SendEllipsisMessageAsync(existingSessionId, request, message, cancellationToken);
            var existingSession = await RetrieveEllipsisSessionAsync(existingSessionId, cancellationToken);
            return new EllipsisSessionResolution(
                existingSessionId,
                false,
                target,
                existingSession,
                TryGetEllipsisProperty(sent, "message", out var sentMessage)
                    ? GetEllipsisString(sentMessage, "id")
                    : GetEllipsisString(sent, "id"));
        }

        JsonElement root;
        if (target.StartsWith(EllipsisAgentModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var agentId = target[EllipsisAgentModelPrefix.Length..].Trim('/');
            if (string.IsNullOrWhiteSpace(agentId) || agentId.Contains('/'))
                throw new InvalidOperationException("Ellipsis saved-agent model ids must use 'agent/{agent_id}'.");

            root = await SendEllipsisJsonAsync(
                HttpMethod.Post,
                $"{EllipsisAgentsEndpoint}/{Uri.EscapeDataString(agentId)}/sessions",
                BuildEllipsisSavedAgentPayload(request, message),
                "Ellipsis run saved agent",
                cancellationToken);
        }
        else
        {
            root = await SendEllipsisJsonAsync(
                HttpMethod.Post,
                EllipsisSessionsEndpoint,
                BuildEllipsisDirectSessionPayload(request, target, message),
                "Ellipsis start session",
                cancellationToken);
        }

        var session = TryGetEllipsisProperty(root, "session", out var nestedSession) ? nestedSession : root;
        var sessionId = GetEllipsisString(session, "id")
                        ?? throw new InvalidOperationException("Ellipsis session response did not include an id.");
        return new EllipsisSessionResolution(sessionId, true, target, session.Clone(), null);
    }

    private Dictionary<string, object?> BuildEllipsisSavedAgentPayload(AIRequest request, string message)
    {
        var payload = new Dictionary<string, object?>();
        var typedInput = GetEllipsisOption<object>(request, "input", "agent_input");
        if (typedInput is null)
            payload["prompt"] = message;
        else
            payload["input"] = typedInput;

        AddEllipsisCommonRunOptions(payload, request, includeImages: false);
        return payload;
    }

    private Dictionary<string, object?> BuildEllipsisDirectSessionPayload(
        AIRequest request,
        string target,
        string message)
    {
        var parts = target.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var harness = NormalizeEllipsisHarness(parts[0]);
        if (harness is not ("claude_code" or "codex"))
            throw new NotSupportedException($"Ellipsis harness '{parts[0]}' is not supported.");

        var harnessConfig = GetEllipsisOption<Dictionary<string, object?>>(request, harness)
                            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        harnessConfig["prompt"] = message;
        if (parts.Length > 1)
            harnessConfig["model"] = string.Join('/', parts.Skip(1));

        var effort = GetEllipsisOption<string>(request, "effort", $"{harness}.effort");
        if (!string.IsNullOrWhiteSpace(effort))
            harnessConfig["effort"] = effort;

        if (harness == "claude_code")
        {
            var maxTurns = GetEllipsisOption<int?>(request, "max_turns", "maxTurns");
            if (maxTurns is > 0)
                harnessConfig["max_turns"] = maxTurns.Value;
            var fallbackModel = GetEllipsisOption<string>(request, "fallback_model", "fallbackModel");
            if (!string.IsNullOrWhiteSpace(fallbackModel))
                harnessConfig["fallback_model"] = fallbackModel;
        }

        var payload = new Dictionary<string, object?> { [harness] = harnessConfig };
        AddEllipsisCommonRunOptions(payload, request, includeImages: true);

        var lifecycle = GetEllipsisOption<object>(request, "lifecycle") as object
                        ?? new Dictionary<string, object?> { ["interactive"] = true };
        payload["lifecycle"] = lifecycle;

        foreach (var option in new[] { "environment", "permissions", "repositories", "skills", "force_rebuild" })
        {
            var value = GetEllipsisOption<object>(request, option);
            if (value is not null)
                payload[option] = value;
        }

        var outputSchema = ExtractEllipsisOutputSchema(request.ResponseFormat);
        if (outputSchema is not null)
            payload["output"] = new Dictionary<string, object?> { ["json_schema"] = outputSchema };

        return payload;
    }

    private void AddEllipsisCommonRunOptions(
        Dictionary<string, object?> payload,
        AIRequest request,
        bool includeImages)
    {
        var budget = GetEllipsisOption<decimal?>(request, "budget");
        if (budget.HasValue)
            payload["budget"] = budget.Value;

        var metadata = GetEllipsisOption<object>(request, "session_metadata", "metadata");
        if (metadata is not null)
            payload["metadata"] = metadata;

        if (includeImages)
        {
            var images = ExtractEllipsisImages(request);
            if (images.Count > 0)
                payload["images"] = images;
        }
    }

    private async Task<JsonElement> SendEllipsisMessageAsync(
        string sessionId,
        AIRequest request,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["idempotency_key"] = GetEllipsisOption<string>(request, "idempotency_key", "idempotencyKey")
                                  ?? CreateEllipsisIdempotencyKey(request, sessionId, message)
        };
        var images = ExtractEllipsisImages(request);
        if (images.Count > 0)
            payload["images"] = images;

        return await SendEllipsisJsonAsync(
            HttpMethod.Post,
            $"{EllipsisSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/messages",
            payload,
            "Ellipsis send message",
            cancellationToken);
    }

    private async Task<JsonElement> RetrieveEllipsisSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var root = await SendEllipsisJsonAsync(
            HttpMethod.Get,
            $"{EllipsisSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}",
            operation: "Ellipsis get session",
            cancellationToken: cancellationToken);
        return TryGetEllipsisProperty(root, "session", out var session) ? session : root;
    }

    private bool TryFindEllipsisSessionId(AIRequest request, out string sessionId)
    {
        sessionId = GetEllipsisOption<string>(request, "sessionId", "session_id")
                    ?? GetEllipsisDictionaryString(request.Input?.Metadata, "sessionId", "session_id")
                    ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return true;

        foreach (var item in request.Input?.Items ?? [])
        {
            if (TryExtractEllipsisSessionId(item.Metadata, out sessionId))
                return true;

            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>() ?? [])
            {
                if (tool.ProviderExecuted != true)
                    continue;
                if (TryExtractEllipsisSessionId(tool.Output, out sessionId)
                    || TryExtractEllipsisSessionId(tool.Metadata, out sessionId)
                    || (string.Equals(tool.ToolName, EllipsisSessionToolName, StringComparison.OrdinalIgnoreCase)
                        && TryExtractEllipsisSessionId(tool.Input, out sessionId)))
                {
                    return true;
                }
            }
        }

        sessionId = string.Empty;
        return false;
    }

    private static bool TryExtractEllipsisSessionId(object? value, out string sessionId)
    {
        sessionId = string.Empty;
        if (value is null)
            return false;

        JsonElement element;
        try
        {
            element = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, EllipsisJson);
        }
        catch
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var name in new[] { "structuredContent", "output", "ellipsis", "session", "metadata" })
        {
            if (TryGetEllipsisProperty(element, name, out var nested)
                && TryExtractEllipsisSessionId(nested, out sessionId))
            {
                return true;
            }
        }

        sessionId = GetEllipsisString(element, "sessionId")
                    ?? GetEllipsisString(element, "session_id")
                    ?? string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static string? GetEllipsisDictionaryString(Dictionary<string, object?>? values, params string[] names)
    {
        if (values is null)
            return null;
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && value is not null)
                return value.ToString();
        }
        return null;
    }

    private IEnumerable<AIStreamEvent> CreateEllipsisSessionToolEvents(
        EllipsisSessionResolution resolution,
        DateTimeOffset timestamp)
    {
        var id = $"ellipsis-create-session-{resolution.Id}";
        var providerMetadata = CreateEllipsisProviderMetadata(new Dictionary<string, object>
        {
            ["sessionId"] = resolution.Id,
            ["session_id"] = resolution.Id,
            ["target"] = resolution.Target,
            ["raw"] = resolution.Session.Clone()
        });

        yield return CreateEllipsisEvent(
            "tool-input-available",
            id,
            new AIToolInputAvailableEventData
            {
                ToolName = EllipsisSessionToolName,
                Title = "Create Ellipsis session",
                Input = new { target = resolution.Target },
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);

        yield return CreateEllipsisEvent(
            "tool-output-available",
            id,
            new AIToolOutputAvailableEventData
            {
                ToolName = EllipsisSessionToolName,
                Output = new CallToolResult
                {
                    Content = [],
                    StructuredContent = JsonSerializer.SerializeToElement(new
                    {
                        sessionId = resolution.Id,
                        session_id = resolution.Id,
                        target = resolution.Target,
                        session = resolution.Session.Clone()
                    }, EllipsisJson)
                },
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            },
            timestamp,
            null);
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamEllipsisWebSocketAsync(
        EllipsisSessionResolution resolution,
        string model,
        EllipsisStreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier())
                  ?? throw new InvalidOperationException("No Ellipsis API key.");
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");

        var streamUri = BuildEllipsisWebSocketUri(resolution.Id, state.LastFeedSequence);
        await socket.ConnectAsync(streamUri, cancellationToken);

        while (socket.State == WebSocketState.Open && !state.Terminal && !cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveEllipsisWebSocketMessageAsync(socket, cancellationToken);
            if (json is null)
                break;

            JsonElement frame;
            try
            {
                frame = JsonSerializer.Deserialize<JsonElement>(json, EllipsisJson).Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            foreach (var streamEvent in MapEllipsisFrame(frame, resolution, model, state))
                yield return streamEvent;
        }
    }

    private static Uri BuildEllipsisWebSocketUri(string sessionId, long afterSequence)
    {
        var query = $"protocol={EllipsisStreamProtocol}";
        if (afterSequence > 0)
            query += $"&after_seq={afterSequence}";
        return new Uri($"wss://api.ellipsis.dev/v1/sessions/{Uri.EscapeDataString(sessionId)}/stream?{query}");
    }

    private static async Task<string?> ReceiveEllipsisWebSocketMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            content.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(content.ToArray());
        }
    }

    private IEnumerable<AIStreamEvent> MapEllipsisFrame(
        JsonElement frame,
        EllipsisSessionResolution resolution,
        string model,
        EllipsisStreamState state)
    {
        var type = GetEllipsisString(frame, "type") ?? GetEllipsisString(frame, "kind") ?? string.Empty;
        switch (type)
        {
            case "snapshot":
                if (TryGetEllipsisProperty(frame, "session", out var snapshotSession))
                {
                    state.Session = snapshotSession;
                    ApplyEllipsisSessionState(snapshotSession, state);
                }
                yield break;
            case "session":
                var session = TryGetEllipsisProperty(frame, "session", out var sessionValue) ? sessionValue : frame;
                state.Session = session;
                ApplyEllipsisSessionState(session, state);
                yield break;
            case "records_append":
                if (!TryGetEllipsisProperty(frame, "records", out var records)
                    || records.ValueKind != JsonValueKind.Array)
                {
                    yield break;
                }
                foreach (var record in records.EnumerateArray())
                {
                    foreach (var streamEvent in MapEllipsisRecord(record, model, state))
                        yield return streamEvent;
                }
                yield break;
            case "delta":
                foreach (var streamEvent in MapEllipsisDelta(frame, resolution.Id, state))
                    yield return streamEvent;
                yield break;
            case "done":
                state.Terminal = true;
                yield break;
            case "error":
                state.Failed = true;
                state.Terminal = true;
                state.Error = GetEllipsisString(frame, "message") ?? GetEllipsisString(frame, "error") ?? "Ellipsis stream error.";
                yield return CreateEllipsisEvent(
                    "error",
                    resolution.Id,
                    new AIErrorEventData { ErrorText = state.Error },
                    DateTimeOffset.UtcNow,
                    CreateEllipsisRawMetadata(frame));
                yield break;
        }
    }

    private IEnumerable<AIStreamEvent> MapEllipsisDelta(
        JsonElement frame,
        string sessionId,
        EllipsisStreamState state)
    {
        var delta = TryGetEllipsisProperty(frame, "delta", out var nested) ? nested : frame;
        var kind = GetEllipsisString(delta, "kind");
        var text = GetEllipsisString(delta, "text");
        if (string.IsNullOrEmpty(text))
            yield break;

        var turnId = GetEllipsisString(delta, "turn_id") ?? state.TurnId ?? sessionId;
        if (kind == "thinking")
        {
            var id = $"ellipsis-reasoning-{turnId}";
            if (state.StartedReasoningIds.Add(id))
                yield return CreateEllipsisEvent("reasoning-start", id, new AIReasoningStartEventData(), DateTimeOffset.UtcNow, null);
            yield return CreateEllipsisEvent("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = text }, DateTimeOffset.UtcNow, null);
        }
        else if (kind == "text")
        {
            var id = $"ellipsis-text-{turnId}";
            if (state.StartedTextIds.Add(id))
                yield return CreateEllipsisEvent("text-start", id, new AITextStartEventData(), DateTimeOffset.UtcNow, null);
            yield return CreateEllipsisEvent("text-delta", id, new AITextDeltaEventData { Delta = text }, DateTimeOffset.UtcNow, null);
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> PollEllipsisSessionAsync(
        EllipsisSessionResolution resolution,
        string model,
        EllipsisStreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var timeout = GetEllipsisOption<TimeSpan?>(new AIRequest { ProviderId = GetIdentifier() }, "poll_timeout")
                      ?? EllipsisPollTimeout;
        var started = DateTimeOffset.UtcNow;

        while (!state.Terminal && !cancellationToken.IsCancellationRequested)
        {
            await foreach (var streamEvent in ReplayEllipsisRecordsAsync(resolution.Id, model, state, cancellationToken))
                yield return streamEvent;

            var session = await RetrieveEllipsisSessionAsync(resolution.Id, cancellationToken);
            state.Session = session;
            ApplyEllipsisSessionState(session, state);
            if (state.Terminal)
                break;

            if (DateTimeOffset.UtcNow - started >= timeout)
                throw new TimeoutException($"Ellipsis session '{resolution.Id}' did not complete within {timeout}.");

            await Task.Delay(EllipsisPollInterval, cancellationToken);
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> ReplayEllipsisRecordsAsync(
        string sessionId,
        string model,
        EllipsisStreamState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var root = await SendEllipsisJsonAsync(
            HttpMethod.Get,
            $"{EllipsisSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/records",
            operation: "Ellipsis list records",
            cancellationToken: cancellationToken);
        if (!TryGetEllipsisProperty(root, "records", out var records) || records.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var record in records.EnumerateArray())
        {
            foreach (var streamEvent in MapEllipsisRecord(record, model, state))
                yield return streamEvent;
        }
    }

    private IEnumerable<AIStreamEvent> MapEllipsisRecord(
        JsonElement record,
        string model,
        EllipsisStreamState state)
    {
        var recordId = GetEllipsisString(record, "id") ?? $"feed-{GetEllipsisInt64(record, "feed_seq")}";
        if (!state.SeenRecordIds.Add(recordId))
            yield break;

        state.LastFeedSequence = Math.Max(state.LastFeedSequence, GetEllipsisInt64(record, "feed_seq") ?? 0);
        state.TurnId = GetEllipsisString(record, "turn_id") ?? state.TurnId;
        var kind = GetEllipsisString(record, "kind") ?? string.Empty;
        var recordType = GetEllipsisString(record, "record_type") ?? string.Empty;
        var timestamp = GetEllipsisDateTimeOffset(record, "created_at") ?? DateTimeOffset.UtcNow;
        var payload = TryGetEllipsisProperty(record, "payload", out var rawPayload) ? rawPayload : record;

        if (kind is "claude_code" or "claude_sdk")
        {
            foreach (var streamEvent in MapEllipsisClaudeRecord(recordId, recordType, payload, record, state, timestamp))
                yield return streamEvent;
            yield break;
        }

        if (kind is "codex_app_server" or "codex" || string.Equals(GetEllipsisString(record, "source"), "codex", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var streamEvent in MapEllipsisCodexRecord(recordId, recordType, payload, record, state, timestamp))
                yield return streamEvent;
            yield break;
        }

        if (kind == "platform")
        {
            foreach (var streamEvent in MapEllipsisPlatformRecord(recordId, recordType, payload, record, state, timestamp))
                yield return streamEvent;
            yield break;
        }

        yield return CreateEllipsisLifecycleDataEvent(recordId, kind, recordType, record, timestamp);
    }

    private IEnumerable<AIStreamEvent> MapEllipsisPlatformRecord(
        string id,
        string type,
        JsonElement payload,
        JsonElement raw,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        if (type == "turn_started")
            state.TurnId = GetEllipsisString(payload, "turn_id") ?? state.TurnId;
        if (type is "turn_completed" or "turn_failed")
        {
            foreach (var streamEvent in CloseEllipsisOpenParts(state, timestamp))
                yield return streamEvent;
        }
        if (type == "turn_failed")
        {
            state.Failed = true;
            state.Error = "Ellipsis turn failed.";
            yield return CreateEllipsisEvent("error", id, new AIErrorEventData { ErrorText = state.Error }, timestamp, CreateEllipsisRawMetadata(raw));
        }

        yield return CreateEllipsisLifecycleToolEvent(id, type, payload, raw, timestamp);
    }

    private IEnumerable<AIStreamEvent> MapEllipsisClaudeRecord(
        string id,
        string recordType,
        JsonElement payload,
        JsonElement raw,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        var nativeType = GetEllipsisString(payload, "type") ?? recordType;
        if (nativeType == "assistant")
        {
            var message = TryGetEllipsisProperty(payload, "message", out var nestedMessage) ? nestedMessage : payload;
            if (!TryGetEllipsisProperty(message, "content", out var content) || content.ValueKind != JsonValueKind.Array)
                yield break;

            var index = 0;
            foreach (var block in content.EnumerateArray())
            {
                var blockId = GetEllipsisString(block, "id") ?? $"{id}-{index++}";
                var blockType = GetEllipsisString(block, "type");
                if (blockType == "text")
                {
                    foreach (var streamEvent in CreateEllipsisCompletedText(blockId, GetEllipsisString(block, "text"), raw, state, timestamp))
                        yield return streamEvent;
                }
                else if (blockType == "thinking")
                {
                    foreach (var streamEvent in CreateEllipsisCompletedReasoning(blockId, GetEllipsisString(block, "thinking") ?? GetEllipsisString(block, "text"), raw, state, timestamp))
                        yield return streamEvent;
                }
                else if (blockType == "tool_use")
                {
                    var toolName = GetEllipsisString(block, "name") ?? "claude_tool";
                    var input = TryGetEllipsisProperty(block, "input", out var toolInput) ? toolInput : block;
                    foreach (var streamEvent in CreateEllipsisToolEvents(blockId, toolName, input, null, raw, false, state, timestamp))
                        yield return streamEvent;
                }
            }
            yield break;
        }

        if (nativeType == "user" && TryGetEllipsisProperty(payload, "message", out var userMessage)
            && TryGetEllipsisProperty(userMessage, "content", out var userContent)
            && userContent.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in userContent.EnumerateArray())
            {
                if (GetEllipsisString(block, "type") != "tool_result")
                    continue;
                var toolId = GetEllipsisString(block, "tool_use_id") ?? id;
                var output = TryGetEllipsisProperty(block, "content", out var toolOutput) ? toolOutput : block;
                foreach (var streamEvent in CreateEllipsisToolEvents(toolId, "claude_tool", new { }, output, raw, GetEllipsisBoolean(block, "is_error") == true, state, timestamp))
                    yield return streamEvent;
            }
            yield break;
        }

        if (nativeType == "result")
        {
            var text = GetEllipsisString(payload, "result");
            foreach (var streamEvent in CreateEllipsisCompletedText(id, text, raw, state, timestamp))
                yield return streamEvent;
            if (GetEllipsisBoolean(payload, "is_error") == true)
            {
                state.Failed = true;
                state.Error = text ?? GetEllipsisString(payload, "subtype") ?? "Ellipsis Claude Code execution failed.";
                yield return CreateEllipsisEvent("error", id, new AIErrorEventData { ErrorText = state.Error }, timestamp, CreateEllipsisRawMetadata(raw));
            }
            yield break;
        }

        yield return CreateEllipsisLifecycleDataEvent(id, "claude_code", recordType, raw, timestamp);
    }

    private IEnumerable<AIStreamEvent> MapEllipsisCodexRecord(
        string id,
        string recordType,
        JsonElement payload,
        JsonElement raw,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        var method = GetEllipsisString(payload, "method") ?? recordType;
        var parameters = TryGetEllipsisProperty(payload, "params", out var nativeParams) ? nativeParams : payload;
        if (method is "item/completed" or "item.completed")
        {
            var item = TryGetEllipsisProperty(parameters, "item", out var nestedItem) ? nestedItem : parameters;
            var itemType = GetEllipsisString(item, "type") ?? string.Empty;
            var itemId = GetEllipsisString(item, "id") ?? id;

            if (itemType is "agentMessage" or "agent_message")
            {
                foreach (var streamEvent in CreateEllipsisCompletedText(itemId, ExtractEllipsisCodexText(item), raw, state, timestamp))
                    yield return streamEvent;
            }
            else if (itemType is "reasoning" or "plan")
            {
                foreach (var streamEvent in CreateEllipsisCompletedReasoning(itemId, ExtractEllipsisCodexText(item), raw, state, timestamp))
                    yield return streamEvent;
            }
            else
            {
                var toolName = NormalizeEllipsisToolName(itemType);
                foreach (var streamEvent in CreateEllipsisToolEvents(itemId, toolName, item, item, raw, IsEllipsisRecordError(item), state, timestamp))
                    yield return streamEvent;
            }
            yield break;
        }

        if (method is "turn/completed" or "turn.completed" or "turn/failed" or "turn.failed")
        {
            foreach (var streamEvent in CloseEllipsisOpenParts(state, timestamp))
                yield return streamEvent;
            if (method.Contains("failed", StringComparison.Ordinal))
            {
                state.Failed = true;
                state.Error = GetEllipsisString(parameters, "error") ?? "Ellipsis Codex turn failed.";
                yield return CreateEllipsisEvent("error", id, new AIErrorEventData { ErrorText = state.Error }, timestamp, CreateEllipsisRawMetadata(raw));
            }
            yield break;
        }

        yield return CreateEllipsisLifecycleDataEvent(id, "codex", method, raw, timestamp);
    }

    private static string? ExtractEllipsisCodexText(JsonElement item)
    {
        foreach (var name in new[] { "text", "content", "message", "summary" })
        {
            if (!TryGetEllipsisProperty(item, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = value.EnumerateArray()
                    .Select(static part => GetEllipsisString(part, "text") ?? (part.ValueKind == JsonValueKind.String ? part.GetString() : null))
                    .Where(static part => !string.IsNullOrWhiteSpace(part));
                return string.Join("\n", parts!);
            }
        }
        return null;
    }

    private IEnumerable<AIStreamEvent> CreateEllipsisCompletedText(
        string id,
        string? text,
        JsonElement raw,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(text) || state.EndedTextIds.Contains(id))
            yield break;
        if (state.StartedTextIds.Add(id))
            yield return CreateEllipsisEvent("text-start", id, new AITextStartEventData(), timestamp, CreateEllipsisRawMetadata(raw));
        yield return CreateEllipsisEvent("text-delta", id, new AITextDeltaEventData { Delta = text }, timestamp, CreateEllipsisRawMetadata(raw));
        state.EndedTextIds.Add(id);
        yield return CreateEllipsisEvent("text-end", id, new AITextEndEventData(), timestamp, CreateEllipsisRawMetadata(raw));
    }

    private IEnumerable<AIStreamEvent> CreateEllipsisCompletedReasoning(
        string id,
        string? text,
        JsonElement raw,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrEmpty(text) || state.EndedReasoningIds.Contains(id))
            yield break;
        if (state.StartedReasoningIds.Add(id))
            yield return CreateEllipsisEvent("reasoning-start", id, new AIReasoningStartEventData(), timestamp, CreateEllipsisRawMetadata(raw));
        yield return CreateEllipsisEvent("reasoning-delta", id, new AIReasoningDeltaEventData { Delta = text }, timestamp, CreateEllipsisRawMetadata(raw));
        state.EndedReasoningIds.Add(id);
        yield return CreateEllipsisEvent("reasoning-end", id, new AIReasoningEndEventData(), timestamp, CreateEllipsisRawMetadata(raw));
    }

    private IEnumerable<AIStreamEvent> CreateEllipsisToolEvents(
        string id,
        string toolName,
        object input,
        object? output,
        JsonElement raw,
        bool isError,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        var providerMetadata = CreateEllipsisProviderMetadata(new Dictionary<string, object>
        {
            ["raw"] = raw.Clone(),
            ["tool_name"] = toolName
        });
        if (state.ToolInputs.Add(id))
        {
            yield return CreateEllipsisEvent("tool-input-available", id, new AIToolInputAvailableEventData
            {
                ToolName = toolName,
                Title = toolName.Replace('_', ' '),
                Input = input,
                ProviderExecuted = true,
                ProviderMetadata = providerMetadata
            }, timestamp, null);
        }
        if (output is null || !state.ToolOutputs.Add(id))
            yield break;
        if (isError)
        {
            yield return CreateEllipsisEvent("tool-output-error", id, new AIToolOutputErrorEventData
            {
                ToolCallId = id,
                ErrorText = output is JsonElement json ? json.GetRawText() : output.ToString() ?? "Ellipsis tool failed.",
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = providerMetadata
            }, timestamp, null);
        }
        else
        {
            yield return CreateEllipsisEvent("tool-output-available", id, new AIToolOutputAvailableEventData
            {
                ToolName = toolName,
                Output = output,
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = providerMetadata
            }, timestamp, null);
        }
    }

    private AIStreamEvent CreateEllipsisLifecycleToolEvent(
        string id,
        string type,
        JsonElement payload,
        JsonElement raw,
        DateTimeOffset timestamp)
    {
        var toolName = NormalizeEllipsisToolName(type);
        var providerMetadata = CreateEllipsisProviderMetadata(new Dictionary<string, object>
        {
            ["raw"] = raw.Clone(),
            ["record_type"] = type
        });
        return CreateEllipsisEvent("tool-output-available", $"ellipsis-lifecycle-{id}", new AIToolOutputAvailableEventData
        {
            ToolName = toolName,
            Output = payload.Clone(),
            ProviderExecuted = true,
            Dynamic = true,
            ProviderMetadata = providerMetadata
        }, timestamp, CreateEllipsisRawMetadata(raw));
    }

    private AIStreamEvent CreateEllipsisLifecycleDataEvent(
        string id,
        string kind,
        string type,
        JsonElement raw,
        DateTimeOffset timestamp)
        => CreateEllipsisEvent($"data-ellipsis.{NormalizeEllipsisToolName(type.Length == 0 ? kind : type)}", id,
            new AIDataEventData { Id = id, Data = raw.Clone(), Transient = false }, timestamp, CreateEllipsisRawMetadata(raw));

    private IEnumerable<AIStreamEvent> CloseEllipsisOpenParts(EllipsisStreamState state, DateTimeOffset timestamp)
    {
        foreach (var id in state.StartedTextIds.Where(id => !state.EndedTextIds.Contains(id)).ToList())
        {
            state.EndedTextIds.Add(id);
            yield return CreateEllipsisEvent("text-end", id, new AITextEndEventData(), timestamp, null);
        }
        foreach (var id in state.StartedReasoningIds.Where(id => !state.EndedReasoningIds.Contains(id)).ToList())
        {
            state.EndedReasoningIds.Add(id);
            yield return CreateEllipsisEvent("reasoning-end", id, new AIReasoningEndEventData(), timestamp, null);
        }
    }

    private async Task EnrichEllipsisTerminalStateAsync(
        string sessionId,
        EllipsisStreamState state,
        CancellationToken cancellationToken)
    {
        state.Session = await RetrieveEllipsisSessionAsync(sessionId, cancellationToken);
        ApplyEllipsisSessionState(state.Session.Value, state);

        var executions = await TryGetEllipsisJsonAsync(
            $"{EllipsisSessionsEndpoint}/{Uri.EscapeDataString(sessionId)}/executions",
            cancellationToken);
        if (executions.HasValue
            && TryGetEllipsisProperty(executions.Value, "executions", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            state.LatestExecution = entries.EnumerateArray()
                .OrderBy(entry => GetEllipsisInt64(entry, "execution_index") ?? 0)
                .LastOrDefault()
                .Clone();
        }
    }

    private static void ApplyEllipsisSessionState(JsonElement session, EllipsisStreamState state)
    {
        var status = GetEllipsisNestedString(session, "lifecycle", "status")
                     ?? GetEllipsisString(session, "status")
                     ?? string.Empty;
        switch (status.ToLowerInvariant())
        {
            case "completed":
            case "closed":
                state.Terminal = true;
                break;
            case "error":
            case "failed":
                state.Terminal = true;
                state.Failed = true;
                state.Error = GetEllipsisNestedString(session, "lifecycle", "detail") ?? "Ellipsis session failed.";
                break;
            case "cancelled":
            case "stopped":
                state.Terminal = true;
                state.Cancelled = true;
                break;
            case "idle":
                // Interactive sessions park at idle after each completed turn.
                state.Terminal = true;
                break;
        }
    }

    private AIStreamEvent CreateEllipsisFinishEvent(
        EllipsisSessionResolution resolution,
        string model,
        EllipsisStreamState state,
        DateTimeOffset timestamp)
    {
        var usage = state.Session.HasValue && TryGetEllipsisProperty(state.Session.Value, "tokens", out var tokens)
            ? tokens.Clone()
            : state.LatestExecution.HasValue && TryGetEllipsisProperty(state.LatestExecution.Value, "tokens", out tokens)
                ? tokens.Clone()
                : JsonSerializer.SerializeToElement(new { }, EllipsisJson);
        var metadata = CreateEllipsisResponseMetadata(resolution, state);
        return CreateEllipsisEvent("finish", resolution.Id, new AIFinishEventData
        {
            FinishReason = state.Failed ? "error" : state.Cancelled ? "cancelled" : "stop",
            Model = model,
            CompletedAt = timestamp.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(
                model,
                timestamp,
                usage,
                additionalProperties: new Dictionary<string, object?> { [GetIdentifier()] = metadata })
        }, timestamp, metadata);
    }

    private AIStreamEvent CreateEllipsisEvent(
        string type,
        string? id,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?>? metadata)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope { Type = type, Id = id, Timestamp = timestamp, Data = data },
            Metadata = metadata
        };

    private Dictionary<string, object?> CreateEllipsisResponseMetadata(
        EllipsisSessionResolution resolution,
        EllipsisStreamState state)
        => new()
        {
            ["ellipsis.sessionId"] = resolution.Id,
            ["ellipsis.session_id"] = resolution.Id,
            ["ellipsis.target"] = resolution.Target,
            ["ellipsis.last_feed_seq"] = state.LastFeedSequence,
            ["ellipsis.session"] = state.Session?.Clone() ?? resolution.Session.Clone(),
            ["ellipsis.execution"] = state.LatestExecution?.Clone(),
            ["ellipsis.error"] = state.Error
        };

    private Dictionary<string, object?> CreateEllipsisRawMetadata(JsonElement raw)
        => new() { ["ellipsis.raw"] = raw.Clone() };

    private Dictionary<string, Dictionary<string, object>> CreateEllipsisProviderMetadata(Dictionary<string, object> metadata)
        => new(StringComparer.OrdinalIgnoreCase) { [GetIdentifier()] = metadata };

    private static Dictionary<string, object?>? FlattenEllipsisProviderMetadata(
        Dictionary<string, Dictionary<string, object>>? metadata)
        => metadata?.ToDictionary(static item => item.Key, static item => (object?)item.Value, StringComparer.OrdinalIgnoreCase);

    private static string NormalizeEllipsisToolName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "ellipsis_event";
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        return builder.ToString().Trim('_');
    }

    private static bool IsEllipsisRecordError(JsonElement value)
        => GetEllipsisBoolean(value, "is_error") == true
           || string.Equals(GetEllipsisString(value, "status"), "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(GetEllipsisString(value, "status"), "error", StringComparison.OrdinalIgnoreCase);
}
