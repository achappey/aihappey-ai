using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Notte;

public partial class NotteProvider
{
    private const string SessionTool = "create_notte_session";
    private sealed class Turn
    {
        public required AIRequest Request { get; init; }
        public required string Alias { get; init; }
        public required JsonObject Options { get; init; }
        public required JsonObject Controls { get; init; }
        public string? SessionId { get; set; }
        public string? AgentId { get; set; }
        public string Status { get; set; } = "completed";
        public List<object> Replies { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<AIContentPart> Parts { get; } = [];
        public AgentActivity Activity { get; } = new();
        public Reply? Last { get; set; }
        public object Values => new { session_id = SessionId, agent_id = AgentId, status = Status, raw = Last?.Raw,
            responses = Replies, warnings = Warnings };
        public Dictionary<string, object?> Metadata => new() { ["notte"] = Values };
    }

    private static Turn Prepare(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var alias = request.Model?.Trim().ToLowerInvariant() ?? "";
        if (alias.StartsWith("notte/")) alias = alias[6..];
        if (alias is not ("agent" or "search" or "scrape")) throw new NotSupportedException($"Unknown Notte model '{request.Model}'.");
        if (request.Tools is { Count: > 0 }) throw new NotSupportedException("Notte does not support gateway client tool definitions.");
        var options = Options(request);
        var controls = options["gateway"] is null ? new JsonObject() : Object(options["gateway"]);
        options.Remove("gateway");
        Control(controls, "wait_seconds", 300, 0.01, 86400);
        Control(controls, "poll_seconds", 2, 0.01, 30);
        return new() { Request = request, Alias = alias, Options = options, Controls = controls };
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        await foreach (var part in Run(turn, cancellationToken)) if (part is not null) CollectPart(turn, part);
        var metadata = turn.Metadata;
        metadata["chatcompletions.response.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = new { notte = turn.Values } }, Json);
        return new()
        {
            ProviderId = "notte", Model = "notte/" + turn.Alias, Status = turn.Status, Metadata = metadata,
            Output = new() { Items = [new() { Type = "message", Role = "assistant", Content = turn.Parts,
                Metadata = new() { ["notte"] = turn.Values, ["chatcompletions.choice.finish_reason"] = turn.Status == "failed" ? "error" : "stop",
                    ["chatcompletions.message.provider_metadata"] = new { notte = turn.Values } } }], Metadata = metadata }
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var turn = Prepare(request);
        var index = 0;
        var toolInputs = new HashSet<string>();
        await foreach (var part in Run(turn, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = $"notte-{Guid.NewGuid():N}-{index++}";
            var metadata = turn.Metadata;
            var scoped = Scoped(turn.Values);
            if (part is null)
            {
                yield return Event("data-notte-agent-status", id, new AIDataEventData { Data = turn.Last?.Raw, Transient = true }, metadata);
                continue;
            }
            if (part is AIToolCallContentPart tool)
            {
                if (tool.Metadata?.TryGetValue("notte", out var native) == true) scoped = Scoped(native!);
                if (toolInputs.Add(tool.ToolCallId))
                {
                    yield return Event("tool-input-start", tool.ToolCallId, new AIToolInputStartEventData { ToolName = tool.ToolName!,
                        Title = tool.Title, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
                    yield return Event("tool-input-available", tool.ToolCallId, new AIToolInputAvailableEventData { ToolName = tool.ToolName!,
                        Title = tool.Title, Input = tool.Input!, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
                }
                if (tool.State == "output-error")
                    yield return Event("tool-output-error", tool.ToolCallId, new AIToolOutputErrorEventData { ToolCallId = tool.ToolCallId,
                        ErrorText = tool.Metadata?.GetValueOrDefault("notte.tool.error") as string ?? "Notte action failed.",
                        ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
                else if (tool.State == "output-available")
                    yield return Event("tool-output-available", tool.ToolCallId, new AIToolOutputAvailableEventData { ToolName = tool.ToolName,
                        Output = tool.Output!, ProviderExecuted = true, ProviderMetadata = scoped }, metadata);
            }
            else if (part is AIReasoningContentPart reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
            {
                if (reasoning.Metadata?.TryGetValue("notte", out var native) == true) scoped = Scoped(native!);
                var reasoningId = reasoning.Metadata?.GetValueOrDefault("notte.activity.id") as string ?? id;
                yield return Event("reasoning-start", reasoningId, new AIReasoningStartEventData { ProviderMetadata = scoped }, metadata);
                yield return Event("reasoning-delta", reasoningId, new AIReasoningDeltaEventData { Delta = reasoning.Text, ProviderMetadata = scoped }, metadata);
                yield return Event("reasoning-end", reasoningId, new AIReasoningEndEventData { ProviderMetadata = scoped }, metadata);
            }
            else if (part is AITextContentPart text)
            {
                var loose = new Dictionary<string, object> { ["notte"] = turn.Values };
                yield return Event("text-start", id, new AITextStartEventData { ProviderMetadata = loose }, metadata);
                yield return Event("text-delta", id, new AITextDeltaEventData { Delta = text.Text, ProviderMetadata = loose }, metadata);
                yield return Event("text-end", id, new AITextEndEventData { ProviderMetadata = loose }, metadata);
            }
            else if (part is AIFileContentPart file)
                yield return Event("file", id, new AIFileEventData { Filename = file.Filename, MediaType = file.MediaType!,
                    Url = $"data:{file.MediaType};base64,{file.Data}", ProviderMetadata = Scoped(file.Metadata ?? metadata) }, metadata);
        }
        var now = DateTimeOffset.UtcNow;
        var final = turn.Metadata;
        final["chatcompletions.stream.raw"] = JsonSerializer.SerializeToElement(new { provider_metadata = new { notte = turn.Values } }, Json);
        yield return Event("finish", request.Id, new AIFinishEventData { Model = "notte/" + turn.Alias,
            FinishReason = turn.Status == "failed" ? "error" : "stop", CompletedAt = now.ToUnixTimeSeconds(),
            MessageMetadata = AIFinishMessageMetadata.Create("notte/" + turn.Alias, now, additionalProperties: turn.Metadata) }, final);
    }

    private async IAsyncEnumerable<AIContentPart?> Run(Turn turn, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (turn.Alias == "agent")
        {
            await foreach (var part in RunAgent(turn, ct)) yield return part;
            yield break;
        }
        var body = turn.Options.DeepClone().AsObject();
        if (turn.Alias == "search")
        {
            body["q"] = Text(turn.Request);
            var depth = body["depth"]?.GetValue<string>() ?? "standard";
            var output = body["outputType"]?.GetValue<string>() ?? "searchResults";
            if (depth is not ("standard" or "fast" or "deep") || output is not ("searchResults" or "sourcedAnswer" or "structured"))
                throw new ArgumentException("Invalid Notte search depth or outputType.");
            var reply = await Record(turn, HttpMethod.Post, "search", body, ct);
            yield return TextPart(SearchText(reply.Raw), turn);
        }
        else
        {
            var urls = ScrapeUrls(turn.Request);
            if (urls.Count == 0) throw new ArgumentException("Notte scrape requires HTTPS URL attachments in the latest user message.");
            if (urls.Count > 50) throw new ArgumentException("Notte scrape accepts at most 50 URLs per request.");
            if (body["instructions"] is null)
            {
                var instructions = string.Join("\n", LatestUser(turn.Request)?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
                if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
            }
            foreach (var url in urls)
            {
                body["url"] = url;
                var reply = await Record(turn, HttpMethod.Post, "scrape", body, ct);
                var structured = Property(reply.Raw, "structured");
                if (structured is { } value && Property(value, "success") is { ValueKind: JsonValueKind.False }) turn.Status = "failed";
                var text = body["response_format"] is not null && structured is { } data && Property(data, "data") is { } result
                    ? result.GetRawText() : String(reply.Raw, "markdown") ?? reply.Raw.GetRawText();
                yield return TextPart($"[{url}](<{url}>)\n\n{text}\n\n", turn);
            }
        }
    }

    private async Task<Reply> Record(Turn turn, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var reply = await Send(turn.Request, method, path, body, ct);
        turn.Last = reply;
        turn.Replies.Add(new { operation = path, raw = reply.Raw, headers = reply.Headers });
        return reply;
    }
    private static AITextContentPart TextPart(string text, Turn turn) => new() { Type = "text", Text = text, Metadata = turn.Metadata };
    private static Dictionary<string, Dictionary<string, object>> Scoped(object raw)
        => new() { ["notte"] = new() { ["raw"] = raw } };
    private static AIStreamEvent Event(string type, string? id, object data, Dictionary<string, object?> metadata)
        => new() { ProviderId = "notte", Metadata = metadata, Event = new() { Type = type, Id = id,
            Timestamp = DateTimeOffset.UtcNow, Data = data, Metadata = metadata } };

    private static string SearchText(JsonElement raw)
    {
        var text = new System.Text.StringBuilder();
        if (String(raw, "answer") is { } answer) text.AppendLine(answer).AppendLine();
        if (Property(raw, "results") is { ValueKind: JsonValueKind.Array } results)
            foreach (var result in results.EnumerateArray())
            {
                var url = String(result, "url");
                var name = String(result, "name") ?? url ?? "Result";
                text.AppendLine(url is null ? name : $"[{name.Replace("[", "\\[").Replace("]", "\\]")}](<{url.Replace(">", "%3E")}>)");
                text.AppendLine(String(result, "content")).AppendLine();
            }
        if (Property(raw, "sources") is { ValueKind: JsonValueKind.Array } sources)
            foreach (var source in sources.EnumerateArray()) text.AppendLine(source.ValueKind == JsonValueKind.String ? source.GetString() : source.GetRawText());
        return text.Length > 0 ? text.ToString().TrimEnd() : raw.GetRawText();
    }
    private static List<string> ScrapeUrls(AIRequest request)
        => (LatestUser(request)?.Content?.OfType<AIFileContentPart>() ?? []).Select(file => file.Data switch
        {
            string text => text,
            Uri uri => uri.ToString(),
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            JsonElement { ValueKind: JsonValueKind.Object } json when Property(json, "file") is { } nested
                => String(nested, "file_url") ?? String(nested, "file_data"),
            _ => null
        }).Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
          .Select(url => url!).Distinct().ToList();
}
