using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Connect0;

public sealed partial class Connect0Provider
{
    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var route = ParseRoute(request.Model);
        var options = Options(request);
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("Connect0 agents execute their tools at the provider. Gateway client tools are not supported.");

        if (route.Kind is not (RouteKind.Agent or RouteKind.ReplayRun or RouteKind.StreamRun))
        {
            foreach (var item in ResponseEvents(await ExecuteUnifiedAsync(request, cancellationToken))) yield return item;
            yield break;
        }

        Reply queued;
        string operation;
        if (route.Kind == RouteKind.Agent)
        {
            operation = "create_run";
            queued = await CreateRun(route, request, options, cancellationToken);
        }
        else if (route.Kind == RouteKind.ReplayRun)
        {
            operation = "replay_run";
            var previousId = RequiredId(request, options, route, "run_id");
            var mode = Option(options, "mode") ?? "in_place";
            if (mode is not ("in_place" or "fork")) throw new ArgumentException("Invalid Connect0 replay mode.");
            var payload = new Dictionary<string, object?> { ["mode"] = mode };
            if (Option(options, "fork_at_message_id", "forkAtMessageId") is { } fork)
            {
                if (!Guid.TryParse(fork, out _)) throw new ArgumentException("Connect0 fork_at_message_id must be a UUID.");
                payload["fork_at_message_id"] = fork;
            }
            if (Property(options, "input_override") is { ValueKind: JsonValueKind.Object } overrideInput)
                payload["input_override"] = overrideInput;
            var agentId = await AgentId(route, cancellationToken);
            queued = await SendJson(HttpMethod.Post,
                $"v1/accounts/{Enc(route.Account)}/agents/{Enc(agentId)}/runs/{Enc(previousId)}/replay", payload, cancellationToken);
        }
        else
        {
            operation = "stream_run";
            var existingId = RequiredId(request, options, route, "run_id");
            queued = await GetRun(route, existingId, cancellationToken);
        }

        var (runId, threadId) = RunIdentity(queued.Raw);
        runId ??= route.Kind == RouteKind.StreamRun ? RequiredId(request, options, route, "run_id") : null;
        if (runId is null) throw new InvalidOperationException("Connect0 did not return a run id for streaming.");
        var metadata = Metadata(route, queued, runId, threadId);
        var receipt = Receipt(route, queued, operation, runId, threadId);
        yield return Event("tool-input-available", receipt.ToolCallId,
            new AIToolInputAvailableEventData
            {
                ToolName = IdentityTool, Input = receipt.Input!, ProviderExecuted = true,
                ProviderMetadata = Scope(metadata)
            }, metadata);
        yield return Event("tool-output-available", receipt.ToolCallId,
            new AIToolOutputAvailableEventData
            {
                ToolName = IdentityTool, Output = receipt.Output!, ProviderExecuted = true,
                ProviderMetadata = Scope(metadata)
            }, metadata);

        if (!IsEndState(String(queued.Raw, "status")))
        {
            var agentId = String(queued.Raw, "agent_id") ?? await AgentId(route, cancellationToken);
            var path = $"v1/accounts/{Enc(route.Account)}/agents/{Enc(agentId)}/runs/{Enc(runId)}/stream";
            using var message = CreateRequest(HttpMethod.Get, path);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(PollTimeout(options));
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            await EnsureSuccess(response, deadline.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var reader = new StreamReader(stream);
            var data = new StringBuilder();
            string? cursor = null, eventName = null;
            try
            {
                while (await reader.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (data.Length > 0 && data.ToString().Trim() != "[DONE]")
                            yield return RawFrame(route, runId, threadId, cursor, eventName, data.ToString(), response);
                        data.Clear(); cursor = eventName = null;
                        continue;
                    }
                    if (line.StartsWith(':')) continue;
                    var separator = line.IndexOf(':');
                    var field = separator < 0 ? line : line[..separator];
                    var value = separator < 0 ? "" : line[(separator + 1)..].TrimStart(' ');
                    switch (field)
                    {
                        case "id": cursor = value; break;
                        case "event": eventName = value; break;
                        case "data": if (data.Length > 0) data.Append('\n'); data.Append(value); break;
                    }
                }
                if (data.Length > 0 && data.ToString().Trim() != "[DONE]")
                    yield return RawFrame(route, runId, threadId, cursor, eventName, data.ToString(), response);
            }
            finally
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // The SSE payload is not specified by Connect0's OpenAPI. Do not invent token deltas;
        // the authoritative run resource supplies the final status and any actual output text.
        var terminal = await GetRun(route, runId, cancellationToken);
        if (!IsEndState(String(terminal.Raw, "status")))
            terminal = await WaitForRun(route, runId, terminal, options, cancellationToken);
        var completed = ToResponse(route, operation, terminal, runId, String(terminal.Raw, "thread_id") ?? threadId);
        foreach (var evt in ResponseEvents(completed, includeTool: false)) yield return evt;
    }

    private async Task<string> AgentId(Route route, CancellationToken ct)
    {
        var identity = (await SendJson(HttpMethod.Get, PublicAgentPath(route), null, ct)).Raw;
        return String(identity, "id") ?? throw new InvalidOperationException("Connect0 agent lookup returned no id.");
    }

    private AIStreamEvent RawFrame(Route route, string runId, string? threadId, string? cursor, string? eventName,
        string content, HttpResponseMessage response)
    {
        object raw;
        try { raw = JsonDocument.Parse(content).RootElement.Clone(); }
        catch (JsonException) { raw = content; }
        var meta = Metadata(route, new Reply(Element(raw), response.StatusCode, Headers(response)), runId, threadId);
        meta["connect0.sse_id"] = cursor;
        meta["connect0.event_name"] = eventName;
        return Event("data-connect0-run-event", cursor ?? runId,
            new AIDataEventData { Id = cursor ?? runId, Data = new { event_name = eventName, cursor, raw } }, meta);
    }

    private AIStreamEvent Event(string kind, string? id, object data, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = GetIdentifier(), Metadata = metadata,
            Event = new AIEventEnvelope { Type = kind, Id = id, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }
        };

    private static Dictionary<string, Dictionary<string, object>> Scope(Dictionary<string, object?> metadata)
        => new() { ["connect0"] = new Dictionary<string, object> { ["raw"] = metadata["connect0"]! } };

    private IEnumerable<AIStreamEvent> ResponseEvents(AIResponse result, bool includeTool = true)
    {
        var metadata = result.Metadata;
        foreach (var part in result.Output?.Items?.SelectMany(item => item.Content ?? []) ?? [])
        {
            if (part is AIToolCallContentPart tool && includeTool)
            {
                yield return Event("tool-input-available", tool.ToolCallId,
                    new AIToolInputAvailableEventData
                    {
                        ToolName = tool.ToolName!, Input = tool.Input!, ProviderExecuted = true,
                        ProviderMetadata = Scope(metadata!)
                    }, metadata);
                yield return Event("tool-output-available", tool.ToolCallId,
                    new AIToolOutputAvailableEventData
                    {
                        ToolName = tool.ToolName, Output = tool.Output!, ProviderExecuted = true,
                        ProviderMetadata = Scope(metadata!)
                    }, metadata);
            }
            if (part is AITextContentPart text)
            {
                var id = $"connect0-text-{Guid.NewGuid():N}";
                yield return Event("text-start", id, new AITextStartEventData(), metadata);
                yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text.Text }, metadata);
                yield return Event("text-end", id, new AITextEndEventData(), metadata);
            }
        }
        var status = result.Status;
        var reason = status is "failed" or "timeout" or "budget_blocked" or "cancelled" ? "error"
            : status is "succeeded" or "completed" ? "stop" : "unknown";
        if (reason == "error")
            yield return Event("error", result.Model, new AIErrorEventData
            {
                ErrorText = String(Property(Element(metadata!["connect0"]), "raw"), "error") ?? $"Connect0 run {status}."
            }, metadata);
        yield return Event("finish", result.Model, new AIFinishEventData
        {
            Model = result.Model, FinishReason = reason,
            MessageMetadata = AIFinishMessageMetadata.Create(result.Model ?? "connect0", DateTimeOffset.UtcNow,
                result.Usage, additionalProperties: new Dictionary<string, object?> { ["connect0"] = metadata?["connect0"] })
        }, metadata);
    }
}
