using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Manus;

public sealed partial class ManusProvider
{
    private sealed class Execution(ManusApiClient api, AIRequest request, JsonElement options, Route route)
    {
        public ManusApiClient Api { get; } = api;
        public AIRequest Request { get; } = request;
        public JsonElement Options { get; } = options;
        public Route Route { get; } = route;
        public JsonObject Body { get; } = Body(options);
        public string? TaskId { get; set; }
        public string? Checkpoint { get; set; }
        public string Status { get; set; } = "running";
        public string Completion { get; set; } = "unknown";
        public JsonElement Created { get; set; } = Empty;
        public JsonElement Detail { get; set; } = Empty;
        public JsonElement Waiting { get; set; } = Empty;
        public JsonElement Structured { get; set; } = Empty;
        public JsonElement Question { get; set; } = Empty;
        public bool ExpectsStructured { get; set; }
        public List<JsonElement> Events { get; } = [];
        public List<AIContentPart> Content { get; } = [];
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var e = NewExecution(request);
        await foreach (var _ in Pipeline(e, cancellationToken)) { }
        var metadata = Meta(e);
        return new AIResponse
        {
            ProviderId = "manus",
            Model = e.Route.Model,
            Status = e.Status == "error" || B(e.Structured, "success") == false ? "failed" : e.Completion == "complete" ? "completed" : "incomplete",
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = e.Content, Metadata = metadata }], Metadata = metadata },
            Usage = new AIUsage(),
            Metadata = metadata
        };
    }
    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var e = NewExecution(request);
        await foreach (var evt in Pipeline(e, cancellationToken)) yield return evt;
    }
    private Execution NewExecution(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = Options(request); // Boundary validation occurs before any upstream request.
        var route = ParseRoute(request.Model);
        if (request.Tools?.Count > 0) throw new NotSupportedException("Manus uses its own tools/connectors/skills, not caller function definitions.");
        return new Execution(Client(), request, options, route);
    }

    private async IAsyncEnumerable<AIStreamEvent> Pipeline(Execution e, [EnumeratorCancellation] CancellationToken ct)
    {
        var receipt = Receipt(e.Request);
        var bodyTask = e.Body["task_id"]?.GetValue<string>();
        e.Body.Remove("task_id");
        e.TaskId = Agree(S(e.Options, "task_id"), bodyTask, "task_id");
        e.TaskId = Agree(e.TaskId, receipt is { } r ? S(r, "task_id") : null, "receipt task_id");
        if (receipt is { } identity && S(identity, "model") is { } previous && previous != e.Route.Model)
            throw new ArgumentException("Manus receipt belongs to a different selectable route.");
        if (e.Route.Profile is { } profile)
        {
            var specified = e.Body["agent_profile"]?.GetValue<string>();
            Agree(profile, specified, "agent_profile");
            e.Body["agent_profile"] = profile;
        }
        if (e.Route.AgentId is { } agentId)
        {
            var agent = P(await e.Api.AgentDetail(agentId, ct), "agent") ?? Empty;
            var main = S(agent, "task_id") ?? throw new InvalidOperationException("Manus agent has no main task.");
            e.TaskId = Agree(e.TaskId, main, "agent main task");
        }
        var pollOnly = B(e.Options, "pollOnly") == true;
        var answer = Answer(e.Request);
        if (pollOnly && answer is not null) throw new ArgumentException("Poll-only resume cannot submit elicitation results.");
        if (e.TaskId is null && (pollOnly || answer is not null)) throw new ArgumentException("Manus resume requires a task receipt or explicit task_id.");
        e.ExpectsStructured = receipt is { } saved && B(saved, "structured_pending") == true;
        if (StructuredSchema(e.Request) is { } schema && !e.Body.ContainsKey("structured_output_schema")) e.Body["structured_output_schema"] = schema;
        e.ExpectsStructured |= e.Body.ContainsKey("structured_output_schema");
        string? start = null;
        var mutation = false;
        if (e.TaskId is not null)
        {
            e.Detail = await e.Api.TaskDetail(e.TaskId, ct);
            var history = await ReadMessages(e, null, ct);
            foreach (var raw in history) Observe(e, raw);
            if (pollOnly)
            {
                start = S(e.Options, "start_event_id") ?? (receipt is { } checkpoint ? S(checkpoint, "last_event_id") : null);
                if (start is not null) e.Seen.Add(start); // listMessages start is inclusive.
            }
            else
            {
                foreach (var raw in history) if (S(raw, "id") is { } id) { e.Seen.Add(id); start = id; }
                if (answer is not null)
                    mutation = await SubmitAnswer(e, answer, ct);
                else
                {
                    if (e.Status == "waiting" && !IsQuestion(S(e.Waiting, "waiting_for_event_type")))
                        throw new InvalidOperationException("Manus requires the current ai_input_required elicitation result before this action can continue.");
                    var followUp = FollowUpBody(e);
                    followUp["message"] = await Message(e, false, ct);
                    e.Created = await e.Api.SendMessage(followUp, ct);
                    mutation = true;
                    // A completed schema is consumed. Omission on a new ordinary turn does not re-arm it.
                    if (e.Completion == "complete") e.ExpectsStructured = e.Body.ContainsKey("structured_output_schema");
                }
                if (mutation) { e.Status = "running"; e.Waiting = Empty; e.Question = Empty; e.Structured = Empty; }
            }
        }
        else
        {
            var create = (JsonObject)e.Body.DeepClone();
            create["message"] = await Message(e, true, ct);
            e.Created = await e.Api.CreateTask(create, ct);
            e.TaskId = S(e.Created, "task_id") ?? throw new InvalidOperationException("Manus create omitted task_id.");
        }
        e.Completion = "unknown";
        foreach (var evt in ReceiptEvents(e)) yield return evt; // Persist identity immediately, even if the client disconnects.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(N(e.Options, "timeoutSeconds") ?? 600, 1, 3600));
        var polls = Math.Clamp(N(e.Options, "maxPolls") ?? 600, 1, 3600);
        var interval = Math.Clamp(N(e.Options, "pollIntervalMs") ?? 1000, 1, 30000);
        for (var poll = 0; poll < polls && DateTimeOffset.UtcNow < deadline; poll++)
        {
            ct.ThrowIfCancellationRequested();
            var events = await ReadMessages(e, start, ct);
            var newEvents = false;
            foreach (var raw in events)
            {
                var id = S(raw, "id") ?? throw new InvalidOperationException("Manus event omitted id.");
                if (!e.Seen.Add(id)) continue;
                newEvents = true; e.Checkpoint = id; e.Events.Add(raw);
                Observe(e, raw);
                foreach (var evt in MapEvent(e, raw)) yield return evt;
            }
            e.Detail = await e.Api.TaskDetail(e.TaskId!, ct);
            var task = P(e.Detail, "task") ?? Empty;
            var currentStatus = S(task, "status");
            // Detail is authoritative; never terminate a follow-up on its historical stopped event.
            if (currentStatus is not null && (!mutation || newEvents || currentStatus == "running")) e.Status = currentStatus;
            if (e.Status == "error") { e.Completion = "failed"; break; }
            if (e.Status == "waiting")
            {
                var form = await WaitingForm(e, ct);
                e.Content.Add(form);
                yield return Event(e, "tool-input-available", form.ToolCallId, new AIToolInputAvailableEventData
                { ToolName = "ai_input_required", Title = form.Title, Input = form.Input!, ProviderExecuted = false, ProviderMetadata = Scoped(Meta(e)) });
                e.Completion = "waiting";
                break;
            }
            if (e.Status == "stopped" && B(task, "has_running_background_jobs") == false && (!mutation || newEvents))
            {
                if (!e.ExpectsStructured || e.Structured.ValueKind == JsonValueKind.Object && P(e.Structured, "value") is not null)
                { e.Completion = "complete"; break; }
            }
            if (poll + 1 < polls && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(interval, Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds))), ct);
        }
        if (e.ExpectsStructured && P(e.Structured, "value") is { } value)
        {
            var text = value.GetRawText();
            e.Content.Add(new AITextContentPart
            {
                Type = "text",
                Text = text,
                Metadata = Meta(e)
            });
            foreach (var evt in TextEvents(e, "manus-structured-" + e.TaskId, text)) yield return evt;
        }
        foreach (var evt in ReceiptEvents(e, final: true)) yield return evt;
        if (e.Status == "error" || B(e.Structured, "success") == false)
            yield return Event(e, "error", e.TaskId, new AIErrorEventData
            {
                ErrorText = "Manus task or structured extraction failed; inspect provider metadata."
            });
        yield return Event(e, "finish", e.TaskId, new AIFinishEventData
        {
            Model = e.Route.Model,
            FinishReason = e.Status == "error" || B(e.Structured, "success") == false ? "error" : e.Completion == "waiting" ? "tool-calls" : e.Completion == "complete" ? "stop" : "other",
            MessageMetadata = AIFinishMessageMetadata.Create(e.Route.Model, DateTimeOffset.UtcNow, additionalProperties: Meta(e))
        });
    }

    private static JsonObject FollowUpBody(Execution e)
    {
        var body = (JsonObject)e.Body.DeepClone();
        foreach (var key in new[] { "project_id", "locale", "interactive_mode", "hide_in_task_list", "share_visibility", "title" }) body.Remove(key);
        body["task_id"] = e.TaskId;
        return body;
    }
    private static async Task<List<JsonElement>> ReadMessages(Execution e, string? start, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        string? cursor = null;
        var cursors = new HashSet<string>();
        do
        {
            var raw = await e.Api.Messages(new
            {
                task_id = e.TaskId,
                order = "asc",
                limit = 200,
                verbose = B(e.Options, "verbose") ?? true,
                slides_format = S(e.Options, "slides_format"),
                cursor,
                start_event_id = cursor is null ? start : null
            }, ct);
            result.AddRange(Array(raw, "messages"));
            if (B(raw, "has_more") != true) break;
            cursor = S(raw, "next_cursor");
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor) || cursors.Count > 1000)
                throw new InvalidOperationException("Invalid Manus messages pagination.");
        } while (true);
        return result;
    }
    private static void Observe(Execution e, JsonElement raw)
    {
        switch (S(raw, "type"))
        {
            case "status_update":
                var status = P(raw, "status_update") ?? Empty;
                e.Status = S(status, "agent_status") ?? e.Status;
                e.Waiting = P(status, "status_detail") ?? Empty;
                break;
            case "assistant_message": e.Question = raw; break;
            case "structured_output_result": e.Structured = P(raw, "structured_output_result") ?? Empty; break;
        }
    }
    private static IEnumerable<AIStreamEvent> MapEvent(Execution e, JsonElement raw)
    {
        var id = S(raw, "id")!;
        var type = S(raw, "type") ?? "unknown";
        yield return Event(e, "data-manus-event", id, new AIDataEventData { Id = id, Data = raw, Transient = false }, raw);
        if (type == "assistant_message")
        {
            var message = P(raw, type) ?? Empty;
            var attachments = Array(message, "attachments");
            var text = ResolveLinks(S(message, "content") ?? "", attachments);
            if (text.Length > 0 && !e.ExpectsStructured)
            {
                e.Content.Add(new AITextContentPart
                {
                    Type = "text",
                    Text = text,
                    Metadata = Meta(e, raw)
                });
                foreach (var evt in TextEvents(e, id, text, raw)) yield return evt;
            }
            foreach (var attachment in attachments)
                if (S(attachment, "url") is { } url && DownloadUrl(url))
                {
                    var mime = S(attachment, "content_type") ?? "application/octet-stream";
                    var filename = S(attachment, "filename");
                    e.Content.Add(new AIFileContentPart
                    {
                        Type = "file",
                        Data = url,
                        MediaType = mime,
                        Filename = filename,
                        Metadata = Meta(e, attachment)
                    });
                    yield return Event(e, "file", id + ":" + filename, new AIFileEventData { Url = url, MediaType = mime, Filename = filename, ProviderMetadata = Scoped(Meta(e, attachment)) });
                }
        }
        else if (type == "explanation")
        {
            var text = S(P(raw, type) ?? Empty, "content") ?? "";
            e.Content.Add(new AIReasoningContentPart
            {
                Type = "reasoning",
                Text = text,
                Metadata = Meta(e, raw)
            });
            yield return Event(e, "reasoning-start", id, new AIReasoningStartEventData { ProviderMetadata = Scoped(Meta(e, raw)) });
            yield return Event(e, "reasoning-delta", id, new AIReasoningDeltaEventData { Delta = text, ProviderMetadata = Scoped(Meta(e, raw)) });
            yield return Event(e, "reasoning-end", id, new AIReasoningEndEventData { ProviderMetadata = Scoped(Meta(e, raw)) });
        }
        else if (type == "tool_used")
        {
            var tool = P(raw, type) ?? Empty;
            var name = S(tool, "tool") ?? "manus_tool";
            var input = P(tool, "params") ?? tool;
            var waiting = e.Status == "waiting" && S(e.Waiting, "waiting_for_event_id") == id || S(tool, "status") == "pending_confirmation";
            var output = P(tool, "result") ?? tool;
            e.Content.Add(Tool(id, name, input, waiting ? null : output, Meta(e, raw), true));
            yield return Event(e, "tool-input-available", id, new AIToolInputAvailableEventData { ToolName = name, Input = input, ProviderExecuted = true, ProviderMetadata = Scoped(Meta(e, raw)) });
            if (!waiting) yield return Event(e, "tool-output-available", id + ":output", new AIToolOutputAvailableEventData
            { ToolName = name, Output = Result(output), ProviderExecuted = true, ProviderMetadata = Scoped(Meta(e, raw)) });
        }
    }
    private static Dictionary<string, object?> Meta(Execution e, JsonElement? raw = null) => new()
    {
        ["manus"] = new
        {
            task_id = e.TaskId,
            model = e.Route.Model,
            agent_id = e.Route.AgentId,
            last_event_id = e.Checkpoint,
            status = e.Status,
            completion_state = e.Completion,
            structured_pending = e.ExpectsStructured && P(e.Structured, "value") is null,
            created = e.Created,
            detail = e.Detail,
            waiting = e.Waiting,
            structured_output = e.Structured,
            raw = raw ?? Empty,
            events = e.Events
        }
    };
    private static Dictionary<string, Dictionary<string, object>> Scoped(Dictionary<string, object?> meta)
        => new() { ["manus"] = El(meta["manus"]).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone()) };
    private static CallToolResult Result(JsonElement raw) => new() { Content = [new TextContentBlock { Text = raw.GetRawText() }], StructuredContent = raw };
    private static AIToolCallContentPart Tool(string id, string name, object input, JsonElement? output, Dictionary<string, object?> metadata, bool executed)
        => new()
        {
            ToolCallId = id,
            Type = "tool-call",
            ToolName = name,
            Input = input,
            Output = output is { } raw ? Result(raw) : null,
            State = output is null ? "input-available" : "output-available",
            ProviderExecuted = executed,
            Metadata = metadata
        };
    private static AIStreamEvent Event(Execution e, string type, string? id, object data, JsonElement? raw = null)
        => new() { ProviderId = "manus", Metadata = Meta(e, raw), Event = new AIEventEnvelope { Type = type, Id = id, Data = data, Timestamp = DateTimeOffset.UtcNow, Metadata = Meta(e, raw) } };
    private static IEnumerable<AIStreamEvent> TextEvents(Execution e, string id, string text, JsonElement? raw = null)
    {
        var metadata = new Dictionary<string, object> { ["manus"] = Meta(e, raw)["manus"]! };
        yield return Event(e, "text-start", id, new AITextStartEventData { ProviderMetadata = metadata });
        yield return Event(e, "text-delta", id, new AITextDeltaEventData { Delta = text, ProviderMetadata = metadata });
        yield return Event(e, "text-end", id, new AITextEndEventData { ProviderMetadata = metadata });
    }
    private static IEnumerable<AIStreamEvent> ReceiptEvents(Execution e, bool final = false)
    {
        var raw = El(new
        {
            task_id = e.TaskId,
            model = e.Route.Model,
            agent_id = e.Route.AgentId,
            last_event_id = e.Checkpoint,
            structured_pending = e.ExpectsStructured && P(e.Structured, "value") is null
        });
        var id = "manus-receipt-" + e.TaskId + (final ? "-checkpoint" : "");
        if (final) e.Content.RemoveAll(p => p is AIToolCallContentPart tool && tool.ToolName == ReceiptTool);
        e.Content.Add(Tool(id, ReceiptTool, raw, raw, Meta(e), true));
        yield return Event(e, "tool-input-available", id, new AIToolInputAvailableEventData { ToolName = ReceiptTool, Input = raw, ProviderExecuted = true, ProviderMetadata = Scoped(Meta(e)) });
        yield return Event(e, "tool-output-available", id + ":output", new AIToolOutputAvailableEventData { ToolName = ReceiptTool, Output = Result(raw), ProviderExecuted = true, ProviderMetadata = Scoped(Meta(e)) });
    }
}
