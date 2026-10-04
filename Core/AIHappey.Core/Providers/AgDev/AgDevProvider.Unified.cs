using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.AgDev;

public partial class AgDevProvider
{
    private const string RunTool = "agdev_run";
    private sealed class Turn
    {
        public required Transport Transport { get; init; }
        public required string AgentId { get; init; }
        public string Model => "agdev/" + AgentId;
        public required JsonObject Body { get; init; }
        public required double WaitSeconds { get; init; }
        public required double PollSeconds { get; init; }
        public required bool IncludeEvents { get; init; }
        public string ToolId { get; } = "agdev-run-" + Guid.NewGuid().ToString("N");
        public string? RunId { get; set; }
        public Reply Reply { get; set; } = new(JsonSerializer.SerializeToElement(new { }), []);
        public Dictionary<string, JsonElement> Events { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];
        public bool WaitTimedOut { get; set; }
    }

    private Turn Prepare(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("AgDev agents execute their configured tools; gateway client tool definitions are not supported.");
        var options = Options(request);
        var controls = options["gateway"] is null ? new JsonObject()
            : options["gateway"] as JsonObject ?? throw new ArgumentException("AgDev gateway controls must be an object.");
        options.Remove("gateway");
        var wait = NumberControl(controls, "wait_seconds", 300, 0, 28800);
        var poll = NumberControl(controls, "poll_seconds", 2, 0.01, 30);
        var events = controls["include_events"]?.GetValue<bool>() ?? true;
        foreach (var (name, value) in Parameters(request)) options[name] = value?.DeepClone();
        return new Turn
        {
            Transport = GetTransport(request),
            AgentId = AgentId(request.Model),
            Body = options,
            WaitSeconds = wait,
            PollSeconds = poll,
            IncludeEvents = events
        };
    }

    private static double NumberControl(JsonObject controls, string name, double fallback, double min, double max)
    {
        var value = controls[name]?.GetValue<double>() ?? fallback;
        if (!double.IsFinite(value) || value < min || value > max)
            throw new ArgumentException($"AgDev gateway.{name} must be between {min} and {max}.");
        return value;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        await foreach (var _ in Run(turn, cancellationToken)) { }
        return ToResponse(turn);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        yield return Event(turn, "tool-input-start", new AIToolInputStartEventData
        {
            ToolName = RunTool,
            Title = "Run AgDev agent",
            ProviderExecuted = true,
            ProviderMetadata = ProviderMetadata(turn)
        });
        yield return Event(turn, "tool-input-available", new AIToolInputAvailableEventData
        {
            ToolName = RunTool,
            Title = "Run AgDev agent",
            Input = turn.Body,
            ProviderExecuted = true,
            ProviderMetadata = ProviderMetadata(turn)
        });
        await foreach (var changedEvents in Run(turn, cancellationToken))
        {
            // The event data schema is deliberately unspecified by AgDev: retain it, do not guess tool/reasoning contracts.
            foreach (var raw in changedEvents)
                yield return Event(turn, "data-agdev-event", new AIDataEventData
                {
                    Id = String(raw, "id"),
                    Data = raw,
                    Transient = false
                }, String(raw, "id"));
            if (IsActive(turn.Reply.Raw))
                yield return Event(turn, "tool-output-available", new AIToolOutputAvailableEventData
                {
                    ToolName = RunTool,
                    Output = ToolResult(turn),
                    ProviderExecuted = true,
                    Preliminary = true,
                    Dynamic = true,
                    ProviderMetadata = ProviderMetadata(turn)
                });
        }
        if (Failed(turn.Reply.Raw))
            yield return Event(turn, "tool-output-error", new AIToolOutputErrorEventData
            {
                ToolCallId = turn.ToolId,
                ErrorText = ErrorText(turn),
                ProviderExecuted = true,
                Dynamic = true,
                ProviderMetadata = ProviderMetadata(turn)
            });
        else
            yield return Event(turn, "tool-output-available", new AIToolOutputAvailableEventData
            {
                ToolName = RunTool,
                Output = ToolResult(turn),
                ProviderExecuted = true,
                Preliminary = false,
                Dynamic = true,
                ProviderMetadata = ProviderMetadata(turn)
            });
        var text = ResultText(turn);
        var textMetadata = new Dictionary<string, object> { ["agdev"] = MetadataValue(turn) };
        var textId = turn.ToolId + "-text";
        yield return Event(turn, "text-start", new AITextStartEventData { ProviderMetadata = textMetadata }, textId);
        yield return Event(turn, "text-delta", new AITextDeltaEventData { Delta = text, ProviderMetadata = textMetadata }, textId);
        yield return Event(turn, "text-end", new AITextEndEventData { ProviderMetadata = textMetadata }, textId);
        foreach (var source in Sources(turn))
            yield return Event(turn, "source-url", new AISourceUrlEventData
            {
                SourceId = turn.ToolId + "-source-" + String(source, "url"),
                Url = String(source, "url")!,
                Title = String(source, "title"),
                ProviderMetadata = new() { ["agdev"] = new() { ["raw"] = source } }
            });
        var now = DateTimeOffset.UtcNow;
        yield return Event(turn, "finish", new AIFinishEventData
        {
            Model = turn.Model,
            FinishReason = FinishReason(turn),
            CompletedAt = now.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create(turn.Model, now,
                additionalProperties: new() { ["agdev"] = MetadataValue(turn) })
        });
    }

    private async IAsyncEnumerable<List<JsonElement>> Run(Turn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        // Each invocation creates a fresh run. POST is never retried or recovered from message history.
        turn.Reply = await SendJson(turn.Transport, HttpMethod.Post, $"agents/{Enc(turn.AgentId)}/runs", turn.Body, ct);
        turn.RunId = String(turn.Reply.Raw, "id") ?? throw new InvalidOperationException("AgDev start-run returned no id.");
        ValidateRun(turn);
        var elapsed = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (turn.WaitSeconds > 0) budget.CancelAfter(TimeSpan.FromSeconds(turn.WaitSeconds));
        // wait_seconds=0 still returns immediately with a complete run snapshot; events are optional supplementary data.
        yield return await FetchEvents(turn, ct);
        while (IsActive(turn.Reply.Raw) && elapsed.Elapsed.TotalSeconds < turn.WaitSeconds)
        {
            var timedOut = false;
            List<JsonElement> events = [];
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(turn.PollSeconds), budget.Token);
                var reply = await SendJson(turn.Transport, HttpMethod.Get,
                    $"agents/{Enc(turn.AgentId)}/runs/{Enc(turn.RunId)}", null, budget.Token);
                turn.Reply = reply;
                ValidateRun(turn);
                events = await FetchEvents(turn, budget.Token);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
            {
                try
                {
                    var retry = (TimeSpan)error.Data["retryAfter"]!;
                    await Task.Delay(retry < TimeSpan.Zero ? TimeSpan.Zero : retry, budget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { timedOut = true; }
            ct.ThrowIfCancellationRequested();
            if (timedOut) break;
            yield return events;
        }
        turn.WaitTimedOut = IsActive(turn.Reply.Raw);
        // No undocumented cancel endpoint: stopping gateway polling does not cancel upstream work.
    }

    private static void ValidateRun(Turn turn)
    {
        if (String(turn.Reply.Raw, "id") != turn.RunId || String(turn.Reply.Raw, "agentId") != turn.AgentId)
            throw new InvalidOperationException("AgDev returned a mismatched agent or run id.");
        if (String(turn.Reply.Raw, "status") is not ("pending" or "running" or "done" or "error"))
            throw new InvalidOperationException("AgDev returned an unknown run status.");
    }

    private async Task<List<JsonElement>> FetchEvents(Turn turn, CancellationToken ct)
    {
        var changed = new List<JsonElement>();
        if (!turn.IncludeEvents) return changed;
        // Re-read from zero to capture changes to unfinished events, not just newly appended entries.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var offset = 0; ; offset += 100)
            {
                var reply = await SendJson(turn.Transport, HttpMethod.Get,
                    $"agents/{Enc(turn.AgentId)}/runs/{Enc(turn.RunId!)}/events?limit=100&offset={offset}", null, ct);
                if (reply.Raw.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("AgDev events must be an array.");
                var page = reply.Raw.EnumerateArray().ToList();
                var added = 0;
                foreach (var raw in page)
                {
                    var id = String(raw, "id") ?? throw new InvalidOperationException("AgDev event has no id.");
                    if (seen.Add(id)) added++;
                    if (!turn.Events.TryGetValue(id, out var previous) || previous.GetRawText() != raw.GetRawText())
                    {
                        turn.Events[id] = raw.Clone();
                        changed.Add(raw.Clone());
                    }
                }
                if (page.Count < 100) break;
                if (added == 0) throw new InvalidOperationException("AgDev event pagination made no progress.");
            }
        }
        catch (HttpRequestException error)
        {
            // Event availability must not turn an otherwise successful run into a failure.
            var warning = error.Message;
            if (!turn.Warnings.Contains(warning)) turn.Warnings.Add(warning);
        }
        return changed;
    }

    private static bool IsActive(JsonElement raw) => String(raw, "status") is "pending" or "running";
    private static bool Failed(JsonElement raw) => String(raw, "status") == "error";
    private static string ErrorText(Turn turn)
        => Property(turn.Reply.Raw, "error") is { } error ? String(error, "message") ?? error.GetRawText() : "AgDev run failed.";
    private static string FinishReason(Turn turn) => Failed(turn.Reply.Raw) ? "error" : IsActive(turn.Reply.Raw) ? "other" : "stop";
    private static string ResultText(Turn turn)
    {
        if (Failed(turn.Reply.Raw)) return ErrorText(turn);
        if (String(turn.Reply.Raw, "status") != "done") return $"AgDev run {turn.RunId}: {String(turn.Reply.Raw, "status")}.";
        var result = Property(turn.Reply.Raw, "resultData");
        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return String(turn.Reply.Raw, "resultType") == "document"
            ? String(result.Value, "result") ?? result.Value.GetRawText() : result.Value.GetRawText();
    }

    private static IEnumerable<JsonElement> Sources(Turn turn)
        => String(turn.Reply.Raw, "resultType") == "document" && Property(turn.Reply.Raw, "resultData") is { } result
            ? Array(result, "sources").Where(s => !string.IsNullOrWhiteSpace(String(s, "url"))).DistinctBy(s => String(s, "url")) : [];
    private static object MetadataValue(Turn turn) => new
    {
        agent_id = turn.AgentId,
        run_id = turn.RunId,
        status = String(turn.Reply.Raw, "status"),
        wait_timed_out = turn.WaitTimedOut,
        raw = turn.Reply.Raw,
        headers = turn.Reply.Headers,
        events = turn.Events.Values.ToArray(),
        warnings = turn.Warnings.ToArray()
    };
    private static Dictionary<string, object?> Metadata(Turn turn) => new() { ["agdev"] = MetadataValue(turn) };
    private static Dictionary<string, Dictionary<string, object>> ProviderMetadata(Turn turn)
        => new() { ["agdev"] = new() { ["raw"] = MetadataValue(turn), ["tool_name"] = RunTool } };
    private static CallToolResult ToolResult(Turn turn) => new()
    {
        IsError = Failed(turn.Reply.Raw),
        StructuredContent = JsonSerializer.SerializeToElement(MetadataValue(turn), Json),
        Content = [new TextContentBlock { Text = turn.Reply.Raw.GetRawText() }]
    };

    private static AIResponse ToResponse(Turn turn)
    {
        var metadata = Metadata(turn);
        metadata["chatcompletions.response.id"] = turn.RunId;
        metadata["messages.response.id"] = turn.RunId;
        metadata["responses.response.id"] = turn.RunId;
        var itemMetadata = Metadata(turn);
        itemMetadata["chatcompletions.choice.finish_reason"] = FinishReason(turn);
        var items = new List<AIOutputItem>
        {
            new()
            {
                Role = "assistant", Metadata = itemMetadata,
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-call",
                        ToolCallId = turn.ToolId, ToolName = RunTool, Title = "Run AgDev agent", Input = turn.Body,
                        Output = ToolResult(turn),
                        State = Failed(turn.Reply.Raw) ? "output-error" : "output-available",
                        ProviderExecuted = true, Metadata = Metadata(turn)
                    },
                    new AITextContentPart {
                        Type = "text",
                        Text = ResultText(turn), Metadata = Metadata(turn) }
                ]
            }
        };
        foreach (var source in Sources(turn))
            items.Add(new AIOutputItem
            {
                Type = "source-url",
                Metadata = new()
                {
                    ["source.url"] = String(source, "url"),
                    ["source.title"] = String(source, "title"),
                    ["chatcompletions.source.url"] = String(source, "url"),
                    ["chatcompletions.source.title"] = String(source, "title"),
                    ["agdev"] = new { raw = source }
                }
            });
        return new AIResponse
        {
            ProviderId = "agdev",
            Model = turn.Model,
            Status = Failed(turn.Reply.Raw) ? "failed" : IsActive(turn.Reply.Raw) ? "in_progress" : "completed",
            Output = new AIOutput { Items = items, Metadata = Metadata(turn) },
            Metadata = metadata
        };
    }

    private static AIStreamEvent Event(Turn turn, string type, object data, string? id = null)
    {
        var metadata = Metadata(turn);
        return new AIStreamEvent
        {
            ProviderId = "agdev",
            Metadata = metadata,
            Event = new AIEventEnvelope { Type = type, Id = id ?? turn.ToolId, Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata }
        };
    }
}
