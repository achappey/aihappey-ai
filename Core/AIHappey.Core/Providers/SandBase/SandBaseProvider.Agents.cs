using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.SandBase;

public partial class SandBaseProvider
{
    private const string AgentPrefix = "agent/";
    private const string SessionTool = "create_sandbase_agent_session";
    private static readonly JsonSerializerOptions AgentJson = JsonSerializerOptions.Web;
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(2);

    private sealed record AgentSession(string Id, string AgentId, bool Created, JsonElement? Raw);

    private sealed class AgentTurn
    {
        public List<object> Entries { get; } = [];
        public Dictionary<string, AgentTool> Tools { get; } = new(StringComparer.Ordinal);
        public JsonElement? Terminal { get; set; }
        public JsonElement? Error { get; set; }
        public int TokensIn { get; set; }
        public int TokensOut { get; set; }
    }

    private sealed class AgentTool
    {
        public required string Id { get; init; }
        public string? Name { get; set; }
        public object? Input { get; set; }
        public object? Output { get; set; }
        public string State { get; set; } = "input-available";
        public Dictionary<string, object?> Metadata { get; } = [];
    }

    private sealed record AgentText(string Text, JsonElement Raw);

    private bool TryResolveSandBaseAgent(string? model, out string agentId)
    {
        agentId = string.Empty;
        if (string.IsNullOrWhiteSpace(model)) return false;
        var value = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (value.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[providerPrefix.Length..];
        if (!value.StartsWith(AgentPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        agentId = value[AgentPrefix.Length..];
        return !string.IsNullOrWhiteSpace(agentId) && !agentId.Contains('/');
    }

    private async Task<IEnumerable<Model>> ListSandBaseAgentModelsAsync(CancellationToken ct)
    {
        var result = new List<Model>();
        string? page = null;
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var url = "v1/agents?limit=100" + (page is null ? "" : "&page=" + Uri.EscapeDataString(page));
            var root = await AgentJsonAsync(HttpMethod.Get, url, null, ct);
            if (!Property(root, "data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            foreach (var agent in data.EnumerateArray())
            {
                var id = String(agent, "id");
                if (string.IsNullOrWhiteSpace(id) ||
                    (Property(agent, "archived_at", out var archived) && archived.ValueKind != JsonValueKind.Null))
                    continue;
                var created = DateTimeOffset.TryParse(String(agent, "created_at"), out var date)
                    ? date.ToUnixTimeSeconds() : (long?)null;
                result.Add(new Model
                {
                    Id = (AgentPrefix + id).ToModelId(GetIdentifier()),
                    Name = String(agent, "name") ?? id,
                    Description = String(agent, "description"),
                    OwnedBy = nameof(SandBase),
                    Type = "language",
                    Tags = ["agent"],
                    Created = created
                });
            }
            page = String(root, "next_page");
        } while (!string.IsNullOrWhiteSpace(page) && seenPages.Add(page));
        return result.DistinctBy(model => model.Id, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> AgentJsonAsync(HttpMethod method, string url, object? payload, CancellationToken ct)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: AgentJson);
        using var response = await _client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SandBase agents API ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        return string.IsNullOrWhiteSpace(body) ? JsonSerializer.SerializeToElement(new { }) : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static bool Property(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? String(JsonElement root, string name)
        => Property(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement root, string name)
        => Property(root, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static JsonElement Json(object value)
        => value is JsonElement element ? element : JsonSerializer.SerializeToElement(value, AgentJson);

    private static string? FindSession(object? source, string agentId)
    {
        if (source is null) return null;
        JsonElement root;
        try { root = Json(source); }
        catch (Exception) { return null; }
        return FindSessionElement(root, agentId, 0);
    }

    private static string? FindSessionElement(JsonElement root, string agentId, int depth)
    {
        if (root.ValueKind != JsonValueKind.Object || depth > 5) return null;
        var foundAgent = String(root, "agentId") ?? String(root, "agent_id") ?? String(root, "agent");
        if (Property(root, "agent", out var agent) && agent.ValueKind == JsonValueKind.Object)
            foundAgent = String(agent, "id") ?? foundAgent;
        if (!string.IsNullOrWhiteSpace(foundAgent) && !string.Equals(foundAgent, agentId, StringComparison.OrdinalIgnoreCase))
            return null;
        var sessionId = String(root, "sessionId") ?? String(root, "session_id");
        if (sessionId is null && String(root, "type") == "session") sessionId = String(root, "id");
        if (sessionId is not null && (foundAgent is not null || depth == 0)) return sessionId;
        foreach (var key in new[] { "structuredContent", "sandbase", "session", "output", "metadata" })
            if (Property(root, key, out var nested) && FindSessionElement(nested, agentId, depth + 1) is { } id)
                return id;
        return null;
    }

    private string? FindContinuation(AIRequest request, string agentId)
    {
        var direct = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "session_id")
                     ?? request.Metadata?.GetProviderOption<string>(GetIdentifier(), "sessionId")
                     ?? FindSession(request.Metadata, agentId)
                     ?? FindSession(request.Input?.Metadata, agentId);
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
                if (tool.ProviderExecuted == true &&
                    (tool.ToolName == SessionTool || tool.ToolCallId?.StartsWith("sandbase-create-session-", StringComparison.Ordinal) == true))
                {
                    var id = FindSession(tool.Output, agentId) ?? FindSession(tool.Metadata, agentId);
                    if (id is not null) return id;
                }
        return null;
    }

    private async Task<AgentSession> ResolveSessionAsync(AIRequest request, string agentId, CancellationToken ct)
    {
        var existing = FindContinuation(request, agentId);
        if (existing is not null)
        {
            var raw = await AgentJsonAsync(HttpMethod.Get, "v1/sessions/" + Uri.EscapeDataString(existing), null, ct);
            if (!string.Equals(String(raw, "agent_id") ?? String(raw, "agent"), agentId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SandBase session belongs to a different agent.");
            return new AgentSession(existing, agentId, false, raw);
        }
        var body = new Dictionary<string, object?> { ["agent"] = agentId };
        var title = request.Metadata?.GetProviderOption<string>(GetIdentifier(), "title");
        if (!string.IsNullOrWhiteSpace(title)) body["title"] = title;
        var metadata = request.Metadata?.GetProviderOption<Dictionary<string, object?>>(GetIdentifier(), "session_metadata");
        if (metadata is not null) body["metadata"] = metadata;
        var session = await AgentJsonAsync(HttpMethod.Post, "v1/sessions", body, ct);
        return new AgentSession(String(session, "id") ?? throw new InvalidOperationException("SandBase session is missing id."), agentId, true, session);
    }

    private static string LatestUserText(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            if (!string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return request.Input?.Text ?? request.Instructions ?? throw new InvalidOperationException("SandBase agent requires a user message.");
    }

    private async Task<string> SendUserAsync(AgentSession session, string text, CancellationToken ct)
    {
        var sent = await AgentJsonAsync(HttpMethod.Post, $"v1/sessions/{Uri.EscapeDataString(session.Id)}/events",
            new { events = new[] { new { type = "user.message", content = new[] { new { type = "text", text } } } } }, ct);
        if (Property(sent, "data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            var id = data.EnumerateArray().Select(e => String(e, "id")).FirstOrDefault(e => e is not null);
            if (id is not null) return id;
        }
        throw new InvalidOperationException("SandBase send events did not return the user event id; delivery outcome is uncertain.");
    }

    private async Task<List<JsonElement>> EventsAfterAsync(string sessionId, string marker, CancellationToken ct)
    {
        var events = new List<JsonElement>();
        string? page = null;
        var pages = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var url = $"v1/sessions/{Uri.EscapeDataString(sessionId)}/events?limit=100" +
                      (page is null ? "" : "&page=" + Uri.EscapeDataString(page));
            var root = await AgentJsonAsync(HttpMethod.Get, url, null, ct);
            if (!Property(root, "data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            events.AddRange(data.EnumerateArray().Select(e => e.Clone()));
            page = String(root, "next_page");
        } while (!string.IsNullOrWhiteSpace(page) && pages.Add(page));
        var index = events.FindIndex(e => String(e, "id") == marker);
        if (index < 0) return [];
        return events.Skip(index + 1).ToList();
    }

    private static bool Terminal(JsonElement e) => String(e, "type") is "session.status_idle" or "session.status_terminated";

    private async Task<List<JsonElement>> WaitForTurnAsync(string sessionId, string marker, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TurnTimeout);
        try
        {
            while (true)
            {
                var events = await EventsAfterAsync(sessionId, marker, timeout.Token);
                if (events.Any(Terminal)) return events.Take(events.FindIndex(Terminal) + 1).ToList();
                await Task.Delay(250, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"SandBase agent session {sessionId} did not finish within {TurnTimeout}.");
        }
    }

    private static string? Text(JsonElement e)
    {
        if (!Property(e, "content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;
        var texts = content.EnumerateArray().Where(x => String(x, "type") == "text")
            .Select(x => String(x, "text")).Where(x => !string.IsNullOrEmpty(x));
        return string.Join("\n", texts);
    }

    private static void ApplyEvent(AgentTurn turn, JsonElement e)
    {
        var type = String(e, "type");
        turn.TokensIn += Number(e, "tokens_in");
        turn.TokensOut += Number(e, "tokens_out");
        switch (type)
        {
            case "agent.message":
                if (Text(e) is { Length: > 0 } text) turn.Entries.Add(new AgentText(text, e.Clone()));
                break;
            case "agent.tool_use":
            case "agent.mcp_tool_use":
                var content = Property(e, "content", out var use) && use.ValueKind == JsonValueKind.Object ? use : e;
                var id = String(e, "id") ?? Guid.NewGuid().ToString("N");
                if (!turn.Tools.TryGetValue(id, out var tool))
                {
                    tool = new AgentTool { Id = id };
                    turn.Tools[id] = tool;
                    turn.Entries.Add(tool);
                }
                tool.Name = String(content, "name") ?? String(e, "name");
                tool.Input = Property(content, "input", out var input) ? input.Clone() : JsonSerializer.SerializeToElement(new { });
                tool.Metadata["sandbase.raw"] = e.Clone();
                break;
            case "agent.tool_result":
            case "agent.mcp_tool_result":
                var useId = String(e, "tool_use_id") ?? String(e, "mcp_tool_use_id");
                if (useId is null) break;
                if (!turn.Tools.TryGetValue(useId, out var resultTool))
                {
                    resultTool = new AgentTool { Id = useId };
                    turn.Tools[useId] = resultTool;
                    turn.Entries.Add(resultTool);
                }
                resultTool.Output = Property(e, "content", out var output) ? output.Clone() : e.Clone();
                resultTool.State = Property(e, "is_error", out var error) && error.ValueKind == JsonValueKind.True
                    ? "output-error" : "output-available";
                resultTool.Metadata["sandbase.result_raw"] = e.Clone();
                break;
            case "session.error": turn.Error = e.Clone(); break;
            case "session.status_idle":
            case "session.status_terminated": turn.Terminal = e.Clone(); break;
        }
    }

    private static string TurnStatus(AgentTurn turn)
    {
        if (turn.Error.HasValue || String(turn.Terminal ?? default, "type") == "session.status_terminated") return "failed";
        if (turn.Terminal is { } terminal && Property(terminal, "stop_reason", out var reason)
            && String(reason, "type") is "retries_exhausted") return "failed";
        return "completed";
    }

    private Dictionary<string, object?> AgentMetadata(AgentSession session, JsonElement? terminal = null, JsonElement? error = null)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["sandbase.sessionId"] = session.Id,
            ["sandbase.agentId"] = session.AgentId,
            ["sandbase.session"] = session.Raw?.Clone()
        };
        if (terminal.HasValue) metadata["sandbase.terminalEvent"] = terminal.Value.Clone();
        if (error.HasValue) metadata["sandbase.errorEvent"] = error.Value.Clone();
        return metadata;
    }

    private Dictionary<string, Dictionary<string, object>> ProviderMetadata(JsonElement raw)
        => new() { [GetIdentifier()] = new Dictionary<string, object> { ["raw"] = raw.Clone() } };

    private AIToolCallContentPart SessionPart(AgentSession session)
    {
        var raw = session.Raw!.Value;
        return new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = $"sandbase-create-session-{session.Id}",
            ToolName = SessionTool,
            Title = "Create SandBase agent session",
            ProviderExecuted = true,
            State = "output-available",
            Input = JsonSerializer.SerializeToElement(new { agent = session.AgentId }),
            Output = new CallToolResult
            {
                Content = [],
                StructuredContent = JsonSerializer.SerializeToElement(new
                {
                    sessionId = session.Id,
                    session_id = session.Id,
                    agentId = session.AgentId,
                    agent_id = session.AgentId,
                    session = raw.Clone()
                }, AgentJson)
            },
            Metadata = new Dictionary<string, object?>
            {
                ["sandbase"] = new { sessionId = session.Id, agentId = session.AgentId, raw = raw.Clone() },
                ["sandbase.raw"] = raw.Clone()
            }
        };
    }

    private async Task<AIResponse> ExecuteSandBaseAgentAsync(AIRequest request, CancellationToken ct)
    {
        if (!TryResolveSandBaseAgent(request.Model, out var agentId)) throw new InvalidOperationException("Invalid SandBase agent model.");
        var text = LatestUserText(request);
        var session = await ResolveSessionAsync(request, agentId, ct);
        var marker = await SendUserAsync(session, text, ct);
        var events = await WaitForTurnAsync(session.Id, marker, ct);
        var turn = new AgentTurn();
        foreach (var e in events) ApplyEvent(turn, e);
        var latest = await AgentJsonAsync(HttpMethod.Get, "v1/sessions/" + Uri.EscapeDataString(session.Id), null, ct);
        session = session with { Raw = latest };
        var items = new List<AIOutputItem>();
        if (session.Created) items.Add(new AIOutputItem { Role = "assistant", Content = [SessionPart(session)] });
        foreach (var entry in turn.Entries)
        {
            AIContentPart part = entry switch
            {
                AgentText t => new AITextContentPart
                {
                    Type = "text",
                    Text = t.Text,
                    Metadata = new() { ["sandbase.raw"] = t.Raw.Clone() }
                },
                AgentTool tool => new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = tool.Id,
                    ToolName = tool.Name,
                    Title = tool.Name,
                    Input = tool.Input,
                    Output = tool.Output,
                    State = tool.State,
                    ProviderExecuted = true,
                    Metadata = tool.Metadata
                },
                _ => throw new InvalidOperationException("Unknown SandBase event.")
            };
            items.Add(new AIOutputItem { Role = "assistant", Content = [part] });
        }
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model!,
            Status = TurnStatus(turn),
            Output = new AIOutput { Items = items },
            Usage = new { input_tokens = turn.TokensIn, output_tokens = turn.TokensOut },
            Metadata = AgentMetadata(session, turn.Terminal, turn.Error)
        };
    }

    private AIStreamEvent StreamEvent(string type, string? id, object data, JsonElement? raw = null)
        => new()
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = raw.HasValue && DateTimeOffset.TryParse(String(raw.Value, "processed_at") ?? String(raw.Value, "created_at"), out var time)
                    ? time : DateTimeOffset.UtcNow,
                Data = data
            },
            Metadata = raw.HasValue ? new() { ["sandbase.raw"] = raw.Value.Clone() } : null
        };

    private IEnumerable<AIStreamEvent> StreamMappedEvent(JsonElement e, AgentTurn turn)
    {
        var type = String(e, "type");
        var id = String(e, "id");
        var useId = String(e, "tool_use_id") ?? String(e, "mcp_tool_use_id");
        ApplyEvent(turn, e);
        switch (type)
        {
            case "agent.message":
                if (Text(e) is not { Length: > 0 } text) break;
                yield return StreamEvent("text-start", id, new AITextStartEventData { ProviderMetadata = new() { ["sandbase"] = e.Clone() } }, e);
                yield return StreamEvent("text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = new() { ["sandbase"] = e.Clone() } }, e);
                yield return StreamEvent("text-end", id, new AITextEndEventData { ProviderMetadata = new() { ["sandbase"] = e.Clone() } }, e);
                break;
            case "agent.tool_use":
            case "agent.mcp_tool_use":
                if (id is null || !turn.Tools.TryGetValue(id, out var tool)) break;
                yield return StreamEvent("tool-input-available", id, new AIToolInputAvailableEventData
                {
                    ToolName = tool.Name ?? "unknown",
                    Title = tool.Name,
                    Input = tool.Input ?? JsonSerializer.SerializeToElement(new { }),
                    ProviderExecuted = true,
                    ProviderMetadata = ProviderMetadata(e)
                }, e);
                break;
            case "agent.tool_result":
            case "agent.mcp_tool_result":
                if (useId is null || !turn.Tools.TryGetValue(useId, out var result)) break;
                yield return StreamEvent("tool-output-available", useId, new AIToolOutputAvailableEventData
                {
                    ToolName = result.Name,
                    Output = result.Output ?? e.Clone(),
                    ProviderExecuted = true,
                    ProviderMetadata = ProviderMetadata(e)
                }, e);
                break;
            case "session.error":
                yield return StreamEvent("error", id, new AIErrorEventData
                {
                    ErrorText = Property(e, "error", out var error) ? String(error, "message") ?? error.ToString() : e.ToString()
                }, e);
                break;
        }
    }

    private async IAsyncEnumerable<JsonElement> ReadSseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length > 0)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal)) lines.Add(line[5..].TrimStart());
                continue;
            }
            if (lines.Count == 0) continue;
            var data = string.Join("\n", lines);
            lines.Clear();
            JsonElement evt;
            try { evt = JsonDocument.Parse(data).RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (evt.ValueKind == JsonValueKind.Object) yield return evt;
        }
        if (lines.Count > 0)
        {
            JsonElement evt;
            try { evt = JsonDocument.Parse(string.Join("\n", lines)).RootElement.Clone(); }
            catch (JsonException) { yield break; }
            if (evt.ValueKind == JsonValueKind.Object) yield return evt;
        }
    }

    private async IAsyncEnumerable<JsonElement> ReadSseWithFallbackAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var events = ReadSseAsync(response, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasNext;
            try { hasNext = await events.MoveNextAsync(); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { yield break; }
            catch (HttpRequestException) when (!ct.IsCancellationRequested) { yield break; }
            catch (IOException) when (!ct.IsCancellationRequested) { yield break; }
            if (!hasNext) yield break;
            yield return events.Current;
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamSandBaseAgentAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!TryResolveSandBaseAgent(request.Model, out var agentId)) throw new InvalidOperationException("Invalid SandBase agent model.");
        var text = LatestUserText(request);
        var session = await ResolveSessionAsync(request, agentId, ct);
        if (session.Created)
        {
            var part = SessionPart(session);
            var metadata = ProviderMetadata(session.Raw!.Value);
            yield return StreamEvent("tool-input-available", part.ToolCallId, new AIToolInputAvailableEventData
            {
                ToolName = SessionTool,
                Title = part.Title,
                Input = part.Input!,
                ProviderExecuted = true,
                ProviderMetadata = metadata
            }, session.Raw);
            yield return StreamEvent("tool-output-available", part.ToolCallId, new AIToolOutputAvailableEventData
            {
                ToolName = SessionTool,
                Output = part.Output!,
                ProviderExecuted = true,
                ProviderMetadata = metadata
            }, session.Raw);
        }

        var marker = await SendUserAsync(session, text, ct);
        var turn = new AgentTurn();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TurnTimeout);
        // The stream replays persisted events before following live updates. The marker
        // separates this turn from earlier turns even when the stream starts late.
        HttpResponseMessage? response = null;
        try
        {
            ApplyAuthHeader();
            using var streamRequest = new HttpRequestMessage(HttpMethod.Get,
                $"v1/sessions/{Uri.EscapeDataString(session.Id)}/events/stream");
            streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            response = await _client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Recover from persisted history below when SSE stops or times out.
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // The event listing endpoint is the fallback for an interrupted SSE stream.
        }

        using (response)
        {
            if (response?.IsSuccessStatusCode == true)
            {
                var afterMarker = false;
                await foreach (var e in ReadSseWithFallbackAsync(response, timeout.Token))
                {
                    var id = String(e, "id");
                    if (!afterMarker)
                    {
                        if (id == marker) afterMarker = true;
                        continue;
                    }
                    if (id is not null && !seen.Add(id)) continue;
                    foreach (var update in StreamMappedEvent(e, turn)) yield return update;
                    if (Terminal(e)) break;
                }
            }
        }

        if (!turn.Terminal.HasValue)
        {
            var history = await WaitForTurnAsync(session.Id, marker, ct);
            foreach (var e in history)
            {
                var id = String(e, "id");
                if (id is not null && !seen.Add(id)) continue;
                foreach (var update in StreamMappedEvent(e, turn)) yield return update;
            }
        }
        var latest = await AgentJsonAsync(HttpMethod.Get, "v1/sessions/" + Uri.EscapeDataString(session.Id), null, ct);
        session = session with { Raw = latest };
        var timestamp = DateTimeOffset.UtcNow;
        var usage = new { input_tokens = turn.TokensIn, output_tokens = turn.TokensOut };
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(),
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = session.Id,
                Timestamp = timestamp,
                Data = new AIFinishEventData
                {
                    FinishReason = TurnStatus(turn) == "failed" ? "error" : "stop",
                    Model = request.Model!,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(request.Model!, timestamp, usage,
                        additionalProperties: new Dictionary<string, object?> { ["sandbase"] = AgentMetadata(session, turn.Terminal, turn.Error) })
                }
            },
            Metadata = AgentMetadata(session, turn.Terminal, turn.Error)
        };
    }
}
