using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Tembo;

public partial class TemboProvider
{
    private static readonly HashSet<string> GatewayControls = new(StringComparer.Ordinal)
    {
        "payload", "pollIntervalSeconds", "pollTimeoutSeconds", "waitForCompletion"
    };
    private static readonly HashSet<string> Harnesses = new(StringComparer.Ordinal)
        { "claudeCode", "codex", "opencode", "cursor", "amp", "fx", "pi" };
    private static readonly string[] ArtifactTypes = ["PullRequest", "ToolCall", "Plan", "Response", "File", "Service", "Source"];

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        AIResponse? response = null;
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
            if (part.Event.Type == "finish")
                response = new AIResponse
                {
                    ProviderId = GetIdentifier(), Model = ModelId(request), Output = part.Event.Output,
                    Status = part.Metadata?["tembo.status"] as string, Metadata = part.Metadata
                };
        return response ?? throw new InvalidOperationException("Tembo execution ended without a final response.");
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = CreatePlan(request);
        var key = ResolveKey(); // Freeze credentials for the entire submission and polling lifecycle.
        using var timeout = new CancellationTokenSource(plan.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        await using var iterator = ExecuteEventsAsync(request, plan, key, linked.Token).GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool moved;
            try { moved = await iterator.MoveNextAsync(); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"Tembo execution exceeded pollTimeoutSeconds ({plan.Timeout.TotalSeconds}). Remote work may still be running; no duplicate submission was made.");
            }
            if (!moved) yield break;
            yield return iterator.Current;
        }
    }

    private async IAsyncEnumerable<AIStreamEvent> ExecuteEventsAsync(AIRequest request, ExecutionPlan plan, string key,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var toolId = "tembo_execution_" + (request.Id ?? Guid.NewGuid().ToString("N"));
        var toolName = plan.AgentId is null ? "tembo_session" : "tembo_agent_run";
        yield return Event("tool-input-start", toolId, new AIToolInputStartEventData
        {
            ToolName = toolName, ProviderExecuted = true, ProviderMetadata = ToolMetadata(toolName, toolId)
        });
        yield return Event("tool-input-delta", toolId, new AIToolInputDeltaEventData { InputTextDelta = plan.Payload.GetRawText() });
        yield return Event("tool-input-available", toolId, new AIToolInputAvailableEventData
        {
            ToolName = toolName, Input = plan.Payload, ProviderExecuted = true, ProviderMetadata = ToolMetadata(toolName, toolId)
        });
        var path = plan.AgentId is null ? "v1/sessions" : $"v1/agents/{Uri.EscapeDataString(plan.AgentId)}/runs";
        var initial = await SendJsonAsync(key, HttpMethod.Post, path, plan.Payload, cancellationToken);
        var final = initial;
        string? sessionId = null;
        string? jobId = null;
        var status = "queued";
        var pollCount = 0;
        var messages = new List<JsonElement>();
        var artifacts = new List<JsonElement>();
        var recordedEvents = new Dictionary<long, JsonElement>();

        if (plan.AgentId is not null)
        {
            jobId = RequiredString(initial, "jobId");
            if (!Guid.TryParse(jobId, out _)) throw new InvalidOperationException("Tembo queued job has an invalid UUID.");
            if (RequiredString(initial, "agentId") != plan.AgentId || RequiredString(initial, "status") != "queued")
                throw new InvalidOperationException("Tembo agent submission returned an unexpected agentId or status.");
            // Intentionally no jobId -> runId conversion: neither the current OpenAPI nor SDK defines it.
        }
        else
        {
            sessionId = RequiredString(initial, "id");
            if (!Guid.TryParse(sessionId, out _)) throw new InvalidOperationException("Tembo session response has an invalid UUID.");
            status = SessionStatus(final);
        }

        yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
        {
            ToolName = toolName, ProviderExecuted = true, Preliminary = plan.Wait && status is "queued" or "inProgress",
            Output = ToolResult(initial), ProviderMetadata = ToolMetadata(toolName, toolId)
        });

        if (sessionId is not null && plan.Wait)
        {
            while (true)
            {
                // Fetch all pages from the beginning: the API does not document cursor direction or an "after" contract.
                // IDs deduplicate repeated pages/polls without assuming that the last item is a resume cursor.
                foreach (var rawEvent in await ListItemsAsync(key, $"v1/sessions/{sessionId}/events", cancellationToken))
                {
                    EnsureSession(rawEvent, sessionId, "event");
                    if (Property(rawEvent, "id") is not { ValueKind: JsonValueKind.Number } id || !id.TryGetInt64(out var eventId) || eventId <= 0)
                        throw new InvalidOperationException("Tembo session event has an invalid numeric ID.");
                    if (!recordedEvents.TryAdd(eventId, rawEvent)) continue;
                    // The documented envelope does not define event-type vocabulary or provider message encoding.
                    // Preserve scope, phases, tools and snapshots verbatim instead of manufacturing text deltas.
                    yield return Event("data-tembo-session-event", $"tembo_event_{eventId}",
                        new AIDataEventData { Id = eventId.ToString(CultureInfo.InvariantCulture), Data = rawEvent, Transient = false });
                }
                if (status is "failed" or "cancelled")
                    throw new InvalidOperationException($"Tembo session '{sessionId}' ended with state '{status}'.");
                if (status == "completed") break;
                await Task.Delay(plan.Interval, cancellationToken);
                final = await SendJsonAsync(key, HttpMethod.Get, $"v1/sessions/{sessionId}", null, cancellationToken);
                if (RequiredString(final, "id") != sessionId)
                    throw new InvalidOperationException("Tembo retrieved a different session than the submitted session.");
                status = SessionStatus(final);
                pollCount++;
            }
            messages = await ListItemsAsync(key, $"v1/messages?sessionId={sessionId}", cancellationToken);
            foreach (var message in messages) EnsureSession(message, sessionId, "message");
            foreach (var type in ArtifactTypes)
            {
                var page = await ListItemsAsync(key, $"v1/artifacts?sessionId={sessionId}&types={type}", cancellationToken);
                foreach (var artifact in page)
                {
                    EnsureSession(artifact, sessionId, "artifact");
                    artifacts.Add(artifact);
                }
            }
            artifacts = artifacts.DistinctBy(artifact => RequiredString(artifact, "id")).ToList();
        }

        var metadata = new Dictionary<string, object?>
        {
            ["tembo.status"] = status, ["tembo.agent_id"] = plan.AgentId, ["tembo.job_id"] = jobId,
            ["tembo.session_id"] = sessionId, ["tembo.session_url"] = String(final, "htmlUrl"),
            ["tembo.wait_for_completion"] = plan.Wait, ["tembo.submitted_payload"] = plan.Payload,
            ["tembo.initial.raw"] = initial, ["tembo.final.raw"] = final, ["tembo.poll_attempt"] = pollCount,
            ["tembo.messages.raw"] = messages, ["tembo.events.raw"] = recordedEvents.Values.ToList(),
            ["tembo.artifacts.raw"] = artifacts
        };
        var output = new List<AIOutputItem>
        {
            new() { Type = "tool-call", Content = [new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = toolId, ToolName = toolName, Input = plan.Payload,
                Output = ToolResult(final), ProviderExecuted = true, State = "output-available", Metadata = metadata
            }] }
        };
        var texts = messages.Where(message => String(message, "role") == "assistant")
            .DistinctBy(message => RequiredString(message, "id"))
            .OrderBy(message => ParseCreatedAt(message))
            .ThenBy(message => RequiredString(message, "id"), StringComparer.Ordinal)
            .Select(message => (Id: "tembo_message_" + RequiredString(message, "id"), Text: RequiredContent(message)))
            .Where(message => !string.IsNullOrEmpty(message.Text)).ToList();
        if (!plan.Wait)
            texts.Add(("tembo_ack_" + toolId, plan.AgentId is null
                ? $"Tembo session created: {sessionId}. Execution completion was not requested."
                : $"Tembo agent run queued: {jobId}. This is a queue acknowledgement, not a completed result."));

        var links = CollectLinks(final, artifacts);
        if (links.Count > 0)
            texts.Add(("tembo_links_" + toolId, string.Join("\n", links.Select(link => $"- [{link.Title}]({link.Url})"))));
        foreach (var text in texts)
        {
            output.Add(new AIOutputItem { Type = "message", Role = "assistant",
                Content = [new AITextContentPart {
                    Type = "text",
                    Text = text.Text }] });
            yield return Event("text-start", text.Id, new AITextStartEventData(), metadata);
            yield return Event("text-delta", text.Id, new AITextDeltaEventData { Delta = text.Text }, metadata);
            yield return Event("text-end", text.Id, new AITextEndEventData(), metadata);
        }
        foreach (var link in links)
            yield return Event("source-url", "tembo_source_" + link.Url, new AISourceUrlEventData
            {
                SourceId = link.Url, Url = link.Url, Title = link.Title
            }, metadata);
        if (plan.Wait)
            yield return Event("tool-output-available", toolId, new AIToolOutputAvailableEventData
            {
                ToolName = toolName, ProviderExecuted = true, Preliminary = false,
                Output = ToolResult(final), ProviderMetadata = ToolMetadata(toolName, toolId)
            }, metadata);
        yield return new AIStreamEvent
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope
            {
                Type = "finish", Id = toolId, Timestamp = DateTimeOffset.UtcNow,
                Output = new AIOutput { Items = output, Metadata = metadata },
                Data = new AIFinishEventData
                {
                    FinishReason = "stop", Model = ModelId(request), CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    MessageMetadata = AIFinishMessageMetadata.Create(ModelId(request), DateTimeOffset.UtcNow,
                        additionalProperties: metadata)
                }
            }
        };
    }

    private static ExecutionPlan CreatePlan(AIRequest request)
    {
        if (request.Tools is { Count: > 0 } || HasValue(request.ToolChoice) || HasValue(request.ResponseFormat))
            throw new NotSupportedException("Tembo does not support upstream tools, tool choice, or structured response formats; configure these in Tembo instead.");
        var local = request.Model?.StartsWith("tembo/", StringComparison.Ordinal) == true ? request.Model[6..] : request.Model;
        string? agentId = null;
        if (local?.StartsWith("agents/", StringComparison.Ordinal) == true)
        {
            agentId = local[7..];
            if (!Guid.TryParse(agentId, out _)) throw new ArgumentException("Tembo agent model must be tembo/agents/{UUID}.");
        }
        else if (local is null || local.Count(character => character == ':') != 1
            || !Harnesses.Contains(local.Split(':')[0]) || string.IsNullOrWhiteSpace(local.Split(':')[1]))
            throw new ArgumentException("Select tembo/agents/{UUID} or tembo/{harness}:{model}. The legacy tembo/agent model is removed.");
        var options = ProviderOptions(request);
        if (options.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tembo provider metadata must be an object.");
        if (Property(options, "keyOrId") is not null)
            throw new NotSupportedException("Tembo keyOrId legacy automation routing is removed. Select an agents/{UUID} model.");
        var wait = Property(options, "waitForCompletion") switch
        {
            null => true, { ValueKind: JsonValueKind.True } => true, { ValueKind: JsonValueKind.False } => false,
            _ => throw new ArgumentException("Tembo waitForCompletion must be boolean.")
        };
        if (agentId is not null && wait)
            throw new NotSupportedException("Tembo's current API returns jobId when queuing an agent but does not document its mapping to runId. Completed agent-run tracking is unsupported; set provider metadata tembo.waitForCompletion=false for explicit queue-only submission. No run has been submitted.");
        var interval = Seconds(options, "pollIntervalSeconds", 5, 0.05, 300);
        var timeout = Seconds(options, "pollTimeoutSeconds", 1800, 0.05, 86400);
        JsonElement payload;
        if (Property(options, "payload") is { } explicitPayload)
        {
            if (explicitPayload.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tembo payload must be an object.");
            payload = explicitPayload.Clone();
        }
        else
        {
            var fields = options.EnumerateObject().Where(property => !GatewayControls.Contains(property.Name))
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
            if (agentId is null)
            {
                if (!fields.ContainsKey("description")) fields.Add("description", BuildPrompt(request));
                fields.TryAdd("agent", local);
                fields.TryAdd("queueRightAway", true);
            }
            else if (!fields.ContainsKey("prompt")) fields.Add("prompt", BuildPrompt(request));
            payload = JsonSerializer.SerializeToElement(fields, TemboJson);
        }
        if (agentId is null)
        {
            if (String(payload, "description") is not { Length: > 0 })
                throw new ArgumentException("Tembo session payload requires a nonempty description; explicit payloads are not augmented.");
            if (Property(payload, "queueRightAway") is { ValueKind: JsonValueKind.False } && wait)
                throw new ArgumentException("Tembo queueRightAway=false requires waitForCompletion=false; an unqueued session cannot be awaited.");
        }
        return new ExecutionPlan(agentId, payload, wait, interval, timeout);
    }

    private static string BuildPrompt(AIRequest request)
    {
        var last = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
        if (last?.Content?.Any(part => part is not AITextContentPart) == true)
            throw new NotSupportedException("Tembo automatic task mapping supports text only. Supply native richContent in an explicit payload for other content.");
        var text = last is null ? request.Input?.Text : string.Join("\n", last.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? []);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Tembo requires a nonempty task or an explicit native payload.");
        var instructions = request.Instructions;
        if (string.IsNullOrWhiteSpace(instructions))
            instructions = string.Join("\n", request.Input?.Items?.Where(item => item.Role is "system" or "developer")
                .SelectMany(item => item.Content?.OfType<AITextContentPart>() ?? []).Select(part => part.Text) ?? []);
        return string.IsNullOrWhiteSpace(instructions) ? text : instructions + "\n\n" + text;
    }

    private static TimeSpan Seconds(JsonElement options, string name, double fallback, double min, double max)
    {
        if (Property(options, name) is not { } value) return TimeSpan.FromSeconds(fallback);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds)
            || !double.IsFinite(seconds) || seconds < min || seconds > max)
            throw new ArgumentException($"Tembo {name} must be a finite number between {min} and {max}.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool HasValue(object? value)
        => value is not null && value is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    private static JsonElement ProviderOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("tembo", out var direct) == true && direct is not null)
            return JsonSerializer.SerializeToElement(direct, TemboJson);
        // Some conversational mappers preserve request metadata in protocol-specific envelopes.
        foreach (var name in new[] { "chatcompletions.request.metadata", "messages.request.metadata" })
        {
            if (request.Metadata?.TryGetValue(name, out var raw) == true && raw is not null)
            {
                var metadata = JsonSerializer.SerializeToElement(raw, TemboJson);
                if (Property(metadata, "tembo") is { } options) return options.Clone();
            }
        }
        return JsonSerializer.SerializeToElement(new { }, TemboJson);
    }

    private static string SessionStatus(JsonElement session)
    {
        var state = Property(session, "state") ?? throw new InvalidOperationException("Tembo session is missing its state object.");
        if (state.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Tembo session state must be an object.");
        var current = String(state, "current");
        if (current is not null && current is not ("queued" or "inProgress" or "completed" or "failed" or "cancelled"))
            throw new NotSupportedException($"Unsupported Tembo session state '{current}'.");
        // current is optional in the schema; the required flags also establish the authoritative state.
        var flags = new[] { ("isFailed", "failed"), ("isCancelled", "cancelled"), ("isCompleted", "completed"), ("isQueued", "queued"), ("inProgress", "inProgress") };
        foreach (var (flag, _) in flags)
            if (Property(state, flag) is not { ValueKind: JsonValueKind.True or JsonValueKind.False })
                throw new InvalidOperationException($"Tembo session state is missing boolean '{flag}'.");
        var active = flags.Where(flag => IsTrue(state, flag.Item1)).Select(flag => flag.Item2).ToList();
        if (active.Count != 1 || (current is not null && active[0] != current))
            throw new InvalidOperationException("Tembo session state flags are ambiguous or inconsistent with current.");
        return active[0];
    }

    private static void EnsureSession(JsonElement item, string sessionId, string kind)
    {
        if (String(item, "sessionId") != sessionId)
            throw new InvalidOperationException($"Tembo returned a {kind} outside the requested session.");
    }
    private static DateTimeOffset ParseCreatedAt(JsonElement message)
        => DateTimeOffset.TryParse(RequiredString(message, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value : throw new InvalidOperationException("Tembo message has an invalid createdAt.");
    private static string RequiredContent(JsonElement message)
        => String(message, "content") ?? throw new InvalidOperationException("Tembo assistant message is missing content.");
    private static string ModelId(AIRequest request)
        => request.Model?.ToModelId("tembo") ?? "tembo";
    private static CallToolResult ToolResult(JsonElement raw)
        => new() { Content = [new TextContentBlock { Text = raw.GetRawText() }], StructuredContent = raw };
    private static Dictionary<string, Dictionary<string, object>> ToolMetadata(string name, string id)
        => new() { ["tembo"] = new() { ["tool_name"] = name, ["tool_use_id"] = id } };
    private static AIStreamEvent Event(string type, string id, object data, Dictionary<string, object?>? metadata = null)
        => new() { ProviderId = "tembo", Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow } };

    private static List<(string Url, string Title)> CollectLinks(JsonElement session, List<JsonElement> artifacts)
    {
        var result = new List<(string Url, string Title)>();
        void Add(string? url, string title)
        {
            if (url is null) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new NotSupportedException("Tembo returned a link that is not an absolute HTTP(S) URL.");
            result.Add((url, title));
        }
        Add(String(session, "htmlUrl"), String(session, "title") ?? "Tembo session");
        if (Property(session, "artifacts") is { ValueKind: JsonValueKind.Array } sessionArtifacts)
            foreach (var artifact in sessionArtifacts.EnumerateArray())
                if (Property(artifact, "pullRequests") is { ValueKind: JsonValueKind.Array } prs)
                    foreach (var pr in prs.EnumerateArray()) Add(String(pr, "url"), String(pr, "title") ?? "Pull request");
        foreach (var artifact in artifacts)
        {
            var title = String(artifact, "title") ?? "Tembo artifact";
            Add(String(artifact, "sourceUrl"), title);
            Add(String(artifact, "serviceUrl"), title);
            if (Property(artifact, "relatedPullRequestUrls") is { ValueKind: JsonValueKind.Array } urls)
                foreach (var url in urls.EnumerateArray())
                    if (url.ValueKind == JsonValueKind.String) Add(url.GetString(), "Pull request");
        }
        return result.DistinctBy(link => link.Url).ToList();
    }

    private sealed record ExecutionPlan(string? AgentId, JsonElement Payload, bool Wait, TimeSpan Interval, TimeSpan Timeout);
}
