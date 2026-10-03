using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.CamelAI;

public sealed partial class CamelAIProvider
{
    private static CancellationTokenSource Deadline(JsonObject options, CancellationToken ct)
    {
        var seconds = options["timeoutSeconds"]?.GetValue<int>() ?? 7200;
        if (seconds is < 1 or > 86400) throw new ArgumentException("CamelAI timeoutSeconds must be 1–86400.");
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(seconds));
        return source;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var agent = AgentId(request.Model);
        var options = Options(request);
        using var deadline = Deadline(options, cancellationToken);
        var raw = await Begin(agent, request, options, deadline.Token);
        raw = await Settle(agent, raw, deadline.Token);
        return await MapResponse(agent, raw, deadline.Token);
    }

    private async Task<JsonElement> Begin(string agent, AIRequest request, JsonObject options, CancellationToken ct)
    {
        // Explicit retrieval and human-input actions never send an extra prompt.
        var operation = Text(options, "operation") ?? "prompt";
        var path = AgentPath(agent);
        switch (operation)
        {
            case "request":
                return await SendJson(HttpMethod.Get, $"{path}/requests/{Uri.EscapeDataString(Required(options, "requestId"))}", null, ct);
            case "inputs":
                return Serialize(new
                {
                    id = "inputs",
                    state = "completed",
                    status = "input_required",
                    outcome = new { result = new { inputs = await SendJson(HttpMethod.Get, $"{path}/inputs?state=pending", null, ct) } }
                });
            case "answer":
                var answer = options["answer"] as JsonObject ?? throw new ArgumentException("CamelAI answer must be an object.");
                var accepted = await SendJson(HttpMethod.Post, $"{path}/inputs/{Uri.EscapeDataString(Required(options, "inputId"))}", answer, ct,
                    Text(options, "idempotencyKey"));
                return Property(accepted, "request") is { ValueKind: JsonValueKind.Object } resume ? resume
                    : Serialize(new { id = "answer", state = "completed", status = "input_required", outcome = new { result = accepted } });
            case "prompt": break;
            default: throw new ArgumentException($"Unknown CamelAI operation '{operation}'.");
        }
        ValidatePrompt(request);
        var body = options["prompt"] is null ? new JsonObject() : Object(options["prompt"], "CamelAI prompt options");
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        var text = latest is null ? request.Input?.Text
            : string.Join("\n", (latest.Content ?? []).OfType<AITextContentPart>().Select(part => part.Text));
        // Raw prompt options take precedence, including explicit text and requestId.
        if (!body.ContainsKey("text")) body["text"] = text ?? "";
        if (body["text"] is not JsonValue value || !value.TryGetValue<string>(out _))
            throw new ArgumentException("CamelAI prompt.text must be a string.");
        if (!body.ContainsKey("requestId")) body["requestId"] = request.Id ?? Guid.NewGuid().ToString("N");
        var requestId = Required(body, "requestId");
        var files = body["files"] as JsonArray ?? new JsonArray();
        foreach (var file in (latest?.Content ?? []).OfType<AIFileContentPart>())
            files.Add(await AttachFile(agent, requestId, file, ct));
        if (files.Count > 0 && body["files"] is null) body["files"] = files;
        if (!body.ContainsKey("output") && request.ResponseFormat is not null)
            body["output"] = StructuredOutput(request.ResponseFormat);
        if (body["output"] is not null && Text(body, "whileRunning") == "steer")
            throw new ArgumentException("CamelRun structured output cannot be used with whileRunning: steer.");
        string? trace = null;
        request.Headers?.TryGetValue("traceparent", out trace);
        return await SendJson(HttpMethod.Post, $"{path}/prompt", body, ct, traceparent: trace);
    }

    private static string Required(JsonObject options, string key) => Text(options, key) is { Length: > 0 } value
        ? value : throw new ArgumentException($"CamelAI {key} is required.");

    private static void ValidatePrompt(AIRequest request)
    {
        if (request.Tools is { Count: > 0 })
            throw new NotSupportedException("CamelRun REST cannot serve gateway client tools. Configure server-executed tools on the agent instead.");
        if (!string.IsNullOrWhiteSpace(request.Instructions) || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true)
            throw new NotSupportedException("An existing CamelAI agent owns its instructions. Configure the agent in CamelRun; gateway instructions do not reconfigure it.");
        // Messages requires max_tokens; it is an envelope requirement, not an agent setting.
        if (request.Temperature is not null || request.TopP is not null || request.ToolChoice is not null
            || request.MaxToolCalls is not null || request.ParallelToolCalls is not null)
            throw new NotSupportedException("CamelAI prompt API does not support gateway sampling or tool-choice settings.");
        var latest = request.Input?.Items?.LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));
        if ((latest?.Content ?? []).Any(part => part is not AITextContentPart and not AIFileContentPart))
            throw new NotSupportedException("CamelAI prompts support text and file attachments only.");
    }

    private static JsonObject StructuredOutput(object format)
    {
        var raw = Object(format, "CamelAI response format");
        var schema = raw["schema"] ?? (raw["json_schema"] as JsonObject)?["schema"];
        if (schema is not JsonObject || (schema["type"]?.GetValue<string>() != "object"))
            throw new NotSupportedException("CamelAI structured output requires an object JSON Schema.");
        return new JsonObject { ["schema"] = schema.DeepClone() };
    }

    private async Task<JsonNode> AttachFile(string agent, string request, AIFileContentPart file, CancellationToken ct)
    {
        if (file.Metadata?.TryGetValue("camelai.path", out var path) == true)
            return new JsonObject { ["path"] = path?.ToString() };
        var encoded = file.Data switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            BinaryData binary => Convert.ToBase64String(binary.ToArray()),
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        } ?? throw new NotSupportedException("CamelAI attachments require inline bytes/base64 or camelai.path; the gateway does not fetch arbitrary URLs.");
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && encoded.Contains(";base64,"))
            encoded = encoded[(encoded.IndexOf(";base64,", StringComparison.Ordinal) + 8)..];
        byte[] data;
        try { data = Convert.FromBase64String(encoded); }
        catch (FormatException ex) { throw new ArgumentException("CamelAI attachment must be base64.", ex); }
        // Upload binary attachments rather than risk the prompt's 4 MiB aggregate inline limit.
        var name = file.Filename ?? $"attachment-{Guid.NewGuid():N}";
        if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new ArgumentException("CamelAI attachment name must not be a path.");
        using var http = CreateRequest(HttpMethod.Put, $"{AgentPath(agent)}/uploads/{Uri.EscapeDataString(request)}/{Uri.EscapeDataString(name)}");
        http.Content = new ByteArrayContent(data);
        http.Content.Headers.ContentType = new(file.MediaType ?? MediaTypeNames.Application.Octet);
        using var response = await _client.SendAsync(http, ct);
        await EnsureSuccess(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new JsonObject { ["path"] = String(document.RootElement, "path") ?? throw new JsonException("CamelAI upload returned no path.") };
    }

    private async Task<JsonElement> Settle(string agent, JsonElement record, CancellationToken ct)
    {
        while (String(record, "state") == "running")
        {
            var id = String(record, "id") ?? throw new JsonException("CamelAI request has no id.");
            record = await SendJson(HttpMethod.Get, $"{AgentPath(agent)}/requests/{Uri.EscapeDataString(id)}?wait=25", null, ct);
            if (String(record, "state") == "running") await Task.Delay(100, ct);
        }
        if (String(record, "state") != "completed") throw new JsonException("CamelAI request has an unknown state.");
        return record;
    }

    private async Task<AIResponse> MapResponse(string agent, JsonElement record, CancellationToken ct)
    {
        var outcome = Property(record, "outcome") ?? Serialize(new { });
        var result = Property(outcome, "result") ?? Serialize(new { });
        var error = String(record, "error") ?? String(outcome, "error") ?? String(result, "error");
        var stopped = String(record, "stopped") ?? String(result, "stopped");
        var status = error is not null || stopped is "spend_limit" or "turn_limit" ? "failed"
            : stopped == "input_required" || String(record, "status") == "input_required" ? "in_progress"
            : String(record, "status") == "failed" ? "failed" : "completed";
        var metadata = Metadata(agent, record);
        var details = (Dictionary<string, object?>)metadata["camelai"]!;
        details["status"] = String(record, "status") ?? (status == "in_progress" ? "input_required" : status);
        details["stopped"] = stopped;
        if (status == "in_progress" && Property(result, "inputs") is null)
            details["inputs"] = await SendJson(HttpMethod.Get, $"{AgentPath(agent)}/inputs?state=pending", null, ct);
        metadata["chatcompletions.response.id"] = String(record, "id");
        metadata["responses.id"] = String(record, "id");
        metadata["messages.response.id"] = String(record, "id");
        if (status == "in_progress") metadata["messages.response.stop_reason"] = "pause_turn";
        var content = new List<AIContentPart>();
        var structured = Property(result, "output");
        var text = structured is { ValueKind: JsonValueKind.Object } ? structured.Value.GetRawText() : String(result, "reply");
        if (text is not null) content.Add(new AITextContentPart
        {
            Type = "tool-call",
            Text = text,
            Metadata = metadata
        });
        // Outcomes contain summaries only, not tool arguments/results. Keep them raw, never fabricate executions.
        if (Property(result, "presented") is { ValueKind: JsonValueKind.Array } presented)
            foreach (var file in presented.EnumerateArray())
                if (String(file, "url") is { } url)
                    content.Add(new AIFileContentPart
                    {
                        Type = "file",
                        Data = url,
                        Filename = String(file, "name"),
                        MediaType = String(file, "contentType"),
                        Metadata = metadata
                    });
        if (error is not null) metadata["responses.error"] = new { code = String(result, "code") ?? "camelai_error", message = error };
        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = $"camelai/agents/{agent}",
            Status = status,
            Metadata = metadata,
            Usage = Usage(Property(result, "usage")),
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata }
        };
    }
    private static AIUsage? Usage(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } value) return null;
        int? Number(string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var count) ? count : null;
        var input = Number("input"); var output = Number("output");
        return new AIUsage
        {
            InputTokens = input,
            OutputTokens = output,
            TotalTokens = input.HasValue && output.HasValue ? input + output : null,
            CachedInputTokens = Number("cacheRead"),
            CacheWriteInputTokens = Number("cacheWrite"),
            ReasoningTokens = Number("reasoning"),
            AdditionalProperties = new() { ["camelai"] = value.Clone() }
        };
    }
}
