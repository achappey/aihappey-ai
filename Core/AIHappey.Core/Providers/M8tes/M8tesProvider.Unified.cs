using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.M8tes;

public partial class M8tesProvider
{
    private const string ReceiptTool = "create_m8tes_run";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    // Operation-local credentials and scope: the provider itself is registered as a singleton.
    private sealed class Conversation(string key, string? userId, long agentId)
    {
        public string Key { get; } = key;
        public string? UserId { get; } = userId;
        public long AgentId { get; } = agentId;
        public string RunId { get; set; } = "";
        public string TurnId { get; } = Guid.NewGuid().ToString("N");
        public bool Followup { get; set; }
        public bool Queued { get; set; }
        public long Baseline { get; set; }
        public string? PreviousOutput { get; set; }
        public JsonObject Raw { get; set; } = new();
        public JsonNode? Outcome { get; set; }
        public JsonNode? Permissions { get; set; }
        public JsonNode? Messages { get; set; }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var conversation = await BeginAsync(request, false, cancellationToken);
        return await FinishAsync(request, conversation, cancellationToken);
    }

    private async Task<Conversation> BeginAsync(AIRequest request, bool streaming, CancellationToken ct)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No M8tes API key.");
        var agentId = ParseAgentId(request.Model);
        var options = Options(request);
        var resolved = request.Metadata?.TryGetValue(M8tesResolvedUserIdMetadataKey, out var value) == true
            ? value?.ToString()
            : _userResolver.Resolve(new ChatRequest { Model = request.Model ?? "", Headers = request.Headers,
                ProviderMetadata = new() { ["m8tes"] = Element(options) } });
        resolved = string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        var explicitScope = Str(options, "user_id");
        if (resolved is not null && explicitScope is not null && resolved != explicitScope)
            throw new ArgumentException("M8tes user_id conflicts with the resolved end-user scope.");
        var c = new Conversation(key, resolved ?? explicitScope, agentId);
        var receipt = FindReceipt(request);
        if (receipt is not null)
        {
            if (Str(receipt, "teammate_id") != agentId.ToString(CultureInfo.InvariantCulture))
                throw new ArgumentException("M8tes run receipt belongs to a different agent. Start a new conversation when switching agents.");
            if (Str(receipt, "user_id") != c.UserId)
                throw new ArgumentException("M8tes run receipt belongs to a different end-user scope.");
        }
        c.RunId = Str(options, "run_id") ?? Str(receipt, "run_id") ?? "";
        c.Followup = c.RunId.Length > 0;
        var latest = request.Input?.Items?.LastOrDefault(i => i.Role == "user");
        var text = string.Join("\n", latest?.Content?.OfType<AITextContentPart>().Select(p => p.Text) ?? []);
        if (text.Length == 0) text = request.Input?.Text ?? "";
        var attachments = (latest?.Content?.OfType<AIFileContentPart>() ?? []).ToList();
        var payload = (JsonObject)options.DeepClone();
        foreach (var field in new[] { "run_id", "task_id", "approve", "answers", "request_id", "idempotency_key" })
            payload.Remove(field);
        payload["stream"] = false; // Obtain identity first, then join the provider's native SSE stream.
        if (c.UserId is not null) payload["user_id"] = c.UserId;
        else payload.Remove("user_id");

        if (request.Tools?.Count > 0)
            throw new NotSupportedException("M8tes cannot execute gateway client tool definitions. Select M8tes tools through provider metadata.");
        if (request.TopP is not null || request.MaxOutputTokens is not null)
            throw new NotSupportedException("M8tes does not support top_p or max_output_tokens. Use its native run options instead.");
        if (request.ResponseFormat is not null && payload["output_schema"] is null)
        {
            var format = Node(request.ResponseFormat);
            var schema = format?["json_schema"]?["schema"] ?? format?["schema"];
            if (schema is null && Str(format, "type") == "json_object")
                schema = new JsonObject { ["type"] = "object" };
            if (schema is null) throw new NotSupportedException("M8tes requires an object JSON Schema for structured output.");
            payload["output_schema"] = schema.DeepClone();
        }
        if (payload["output_schema"] is JsonNode outputSchema)
            ValidateSchema(outputSchema);

        if (c.Followup)
        {
            c.Raw = await GetRunAsync(c, ct);
            ValidateRun(c, c.Raw);
            c.PreviousOutput = Str(c.Raw, "output");
            var history = await SendJsonAsync(c, HttpMethod.Get, $"runs/{Id(c.RunId)}/messages?tail=true&limit=1", null, ct);
            c.Baseline = Rows(history).Select(m => Number(m, "sequence")).DefaultIfEmpty(0).Max();
            var approval = options["approve"] as JsonObject;
            var answers = options["answers"];
            var nativeApproval = request.Input?.Items?.SelectMany(i => i.Content ?? [])
                .OfType<AIToolCallContentPart>().LastOrDefault(t => t.Approval?.Approved is not null && t.State == "approval-responded");
            if (approval is null && nativeApproval?.Approval is { } a)
                approval = new JsonObject { ["request_id"] = a.Id, ["decision"] = a.Approved == true ? "allow" : "deny", ["reason"] = a.Reason };
            if (approval is not null || answers is not null)
            {
                if (attachments.Count > 0) throw new NotSupportedException("M8tes approval/answer resumes do not accept attachments.");
                if (approval is not null)
                    await SendJsonAsync(c, HttpMethod.Post, $"runs/{Id(c.RunId)}/approve", approval, ct);
                if (answers is not null)
                    await SendJsonAsync(c, HttpMethod.Post, $"runs/{Id(c.RunId)}/answer",
                        new JsonObject { ["answers"] = answers.DeepClone(), ["request_id"] = options["request_id"]?.DeepClone() }, ct);
                c.Raw = await GetRunAsync(c, ct);
                return c;
            }
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("M8tes requires a latest user message to continue a run.");
            payload.Remove("user_id"); // Reply schema inherits scope; it does not accept user_id.
            payload.Remove("teammate_id");
            payload["message"] = text;
            c.Raw = Object(await SubmitAsync(c, $"runs/{Id(c.RunId)}/reply", payload, attachments, Str(options, "idempotency_key"), ct));
        }
        else if (Str(options, "task_id") is { } taskId)
        {
            if (attachments.Count > 0 || !string.IsNullOrWhiteSpace(text))
                throw new NotSupportedException("A saved M8tes task uses its own instructions and cannot accept a chat message or attachments. Omit input when executing a saved task.");
            var task = await SendJsonAsync(c, HttpMethod.Get, $"tasks/{Id(taskId)}{ScopeQuery(c)}", null, ct);
            if (Str(task, "teammate_id") != agentId.ToString(CultureInfo.InvariantCulture))
                throw new ArgumentException("M8tes task belongs to a different agent.");
            payload.Remove("teammate_id");
            c.Raw = Object(await SubmitAsync(c, $"tasks/{Id(taskId)}/runs", payload, [], Str(options, "idempotency_key"), ct));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("M8tes requires a latest user message.");
            payload["message"] = text;
            payload["teammate_id"] = agentId;
            var instructions = request.Instructions ?? string.Join("\n", request.Input?.Items?
                .Where(i => i.Role is "system" or "developer").SelectMany(i => i.Content ?? [])
                .OfType<AITextContentPart>().Select(p => p.Text) ?? []);
            // Existing agents cannot receive an instructions override: retain supplied context in the message.
            if (!string.IsNullOrWhiteSpace(instructions)) payload["message"] = instructions + "\n\n" + text;
            c.Raw = Object(await SubmitAsync(c, "runs", payload, attachments, Str(options, "idempotency_key"), ct));
        }
        c.RunId = Str(c.Raw, "id") ?? throw new InvalidOperationException("M8tes did not return a run id.");
        ValidateRun(c, c.Raw);
        c.Queued = Str(c.Raw, "delivery") == "queued";
        return c;
    }

    private async Task<AIResponse> FinishAsync(AIRequest request, Conversation c, CancellationToken ct)
    {
        // A queued reply has not executed. Return its receipt, never the preceding turn's output.
        if (!c.Queued)
        {
            while (!StreamSettled(Str(c.Raw, "status")))
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                c.Raw = await GetRunAsync(c, ct);
                ValidateRun(c, c.Raw);
            }
            c.Outcome = await SendJsonAsync(c, HttpMethod.Get, $"runs/{Id(c.RunId)}/outcome", null, ct);
            c.Messages = await ReadTurnMessagesAsync(c, ct);
            if (Str(c.Raw, "status") is "paused" or "awaiting_approval")
                c.Permissions = await SendJsonAsync(c, HttpMethod.Get, $"runs/{Id(c.RunId)}/permissions", null, ct);
        }
        var parts = new List<AIContentPart>();
        if (!c.Queued)
        {
            AddMessageParts(c.Messages, parts);
            if (!parts.OfType<AITextContentPart>().Any())
            {
                var text = c.Followup ? Str(c.Outcome, "summary") : Str(c.Raw, "output") ?? Str(c.Outcome, "summary");
                if (c.Followup && text == c.PreviousOutput) text = null;
                if (!string.IsNullOrEmpty(text)) parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = RawMetadata(c.Raw) });
            }
            if (request.ResponseFormat is not null && c.Raw["output_data"] is { } data)
            {
                parts.RemoveAll(p => p is AITextContentPart);
                parts.Add(new AITextContentPart { Type = "text", Text = data.ToJsonString(), Metadata = RawMetadata(c.Raw) });
            }
            foreach (var gate in Rows(c.Permissions).Where(p => Str(p, "status") == "pending"))
                parts.Add(new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = Str(gate, "request_id") ?? Guid.NewGuid().ToString("N"), ToolName = Str(gate, "tool_name"),
                    Input = Element(gate["tool_input"]), ProviderExecuted = true, State = "approval-requested",
                    Approval = new AIToolCallApproval { Id = Str(gate, "request_id") }, Metadata = RawMetadata(gate)
                });
            if (Str(c.Raw, "status") is "completed" or "closed")
                await AddFilesAsync(c, parts, ct);
        }
        parts.Insert(0, Receipt(c));
        var metadata = RawMetadata(new { run = Element(c.Raw), outcome = Element(c.Outcome), permissions = Element(c.Permissions) });
        var usage = c.Raw["usage"] ?? c.Outcome;
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = c.Queued ? "queued" : Str(c.Raw, "status"),
            Metadata = metadata, Usage = Element(usage),
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = parts, Metadata = metadata }], Metadata = metadata }
        };
    }

    private async Task<JsonArray> ReadTurnMessagesAsync(Conversation c, CancellationToken ct)
    {
        var messages = new JsonArray();
        var cursor = c.Baseline;
        while (true)
        {
            var page = Rows(await SendJsonAsync(c, HttpMethod.Get,
                $"runs/{Id(c.RunId)}/messages?after_sequence={cursor}&limit=500", null, ct)).ToList();
            foreach (var message in page.Where(m => Number(m, "sequence") > c.Baseline)) messages.Add(message.DeepClone());
            if (page.Count < 500) break;
            var next = page.Max(m => Number(m, "sequence"));
            if (next <= cursor) throw new JsonException("M8tes message pagination did not advance.");
            cursor = next;
        }
        return messages;
    }

    private void AddMessageParts(JsonNode? messages, List<AIContentPart> parts)
    {
        var tools = new Dictionary<string, AIToolCallContentPart>();
        foreach (var message in Rows(messages))
        {
            var blocks = message["content_blocks"] as JsonArray;
            if (blocks is null && Str(message, "role") == "assistant" && Str(message, "content") is { Length: > 0 } text)
                parts.Add(new AITextContentPart { Type = "text", Text = text, Metadata = RawMetadata(message) });
            foreach (var block in blocks?.OfType<JsonObject>() ?? [])
            {
                switch (Str(block, "type"))
                {
                    case "text" when Str(message, "role") == "assistant":
                        parts.Add(new AITextContentPart { Type = "text", Text = Str(block, "text") ?? "", Metadata = RawMetadata(block) }); break;
                    case "thinking":
                        parts.Add(new AIReasoningContentPart { Type = "reasoning", Text = Str(block, "thinking"), Signature = Str(block, "signature"), Metadata = RawMetadata(block) }); break;
                    case "tool_use":
                        var id = Str(block, "id") ?? Guid.NewGuid().ToString("N");
                        var tool = new AIToolCallContentPart { Type = "tool-call", ToolCallId = id, ToolName = Str(block, "name"), Input = Element(block["input"]), ProviderExecuted = true, State = "input-available", Metadata = RawMetadata(block) };
                        tools[id] = tool; parts.Add(tool); break;
                    case "tool_result":
                        var callId = Str(block, "tool_use_id") ?? "";
                        tools.TryGetValue(callId, out var original);
                        if (original is not null) parts.Remove(original);
                        var error = block["is_error"]?.ToString() == "true";
                        parts.Add(new AIToolCallContentPart { Type = "tool-call", ToolCallId = callId, ToolName = original?.ToolName ?? "m8tes_tool", Input = original?.Input,
                            Output = new CallToolResult { IsError = error, StructuredContent = Element(block["result"] ?? block["content"]) },
                            ProviderExecuted = true, State = error ? "output-error" : "output-available", Metadata = RawMetadata(block) }); break;
                }
            }
        }
    }

    private AIToolCallContentPart Receipt(Conversation c) => new()
    {
        Type = "tool-call",
        ToolCallId = $"m8tes-run-{c.RunId}-{c.TurnId}", ToolName = ReceiptTool, Title = "M8tes run",
        Input = new { teammate_id = c.AgentId }, ProviderExecuted = true, State = "output-available",
        Output = new CallToolResult { StructuredContent = Element(new
        {
            type = ReceiptTool, provider = "m8tes", run_id = c.RunId, teammate_id = c.AgentId,
            task_id = c.Raw["task_id"], user_id = c.UserId, status = c.Queued ? "queued" : Str(c.Raw, "status"),
            delivery = c.Raw["delivery"], queued_message_id = c.Raw["queued_message_id"],
            pending = c.Permissions, output_data = c.Raw["output_data"] ?? c.Outcome?["output_data"],
            files = c.Raw["files"], usage = c.Raw["usage"], outcome = c.Outcome, raw = c.Raw
        }) }, Metadata = RawMetadata(c.Raw)
    };

    private static JsonObject? FindReceipt(AIRequest request)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
        {
            foreach (var tool in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
                if (FindMarked(Node(tool.Output)) is { } receipt) return receipt;
            if (FindMarked(Node(item.Metadata)) is { } fromMetadata) return fromMetadata;
        }
        // Protocol mappers preserve original tool-result shapes here, including lost execution flags.
        return FindMarked(Node(request.Input?.Metadata));
    }

    private static JsonObject? FindMarked(JsonNode? value, int depth = 0)
    {
        if (depth > 24) return null;
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
        { try { return FindMarked(JsonNode.Parse(text), depth + 1); } catch (JsonException) { return null; } }
        if (value is JsonObject obj)
        {
            if (Str(obj, "type") == ReceiptTool && Str(obj, "provider") == "m8tes" && Str(obj, "run_id") is not null) return obj;
            foreach (var property in obj.Reverse()) if (FindMarked(property.Value, depth + 1) is { } nested) return nested;
        }
        if (value is JsonArray array)
            foreach (var child in array.Reverse()) if (FindMarked(child, depth + 1) is { } nested) return nested;
        return null;
    }

    private static JsonObject Options(AIRequest request)
    {
        if (request.Metadata is null) return new();
        if (request.Metadata.TryGetValue("m8tes", out var direct)) return Object(Node(direct));
        foreach (var entry in request.Metadata)
        {
            var node = Node(entry.Value);
            if (node is JsonObject obj && obj["m8tes"] is JsonObject scoped) return (JsonObject)scoped.DeepClone();
            if (node is JsonObject raw && raw["providerMetadata"]?["m8tes"] is JsonObject camel) return (JsonObject)camel.DeepClone();
        }
        return new();
    }

    private static long ParseAgentId(string? model)
    {
        var slug = model?.Trim().Trim('/') ?? "";
        if (slug.StartsWith("m8tes/", StringComparison.OrdinalIgnoreCase)) slug = slug[6..];
        if (slug.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)) slug = slug[6..];
        if (!long.TryParse(slug, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("M8tes requires model m8tes/{agent_id}.");
        return id;
    }
    private static void ValidateSchema(JsonNode schema)
    {
        if (Str(schema, "type") != "object") throw new ArgumentException("M8tes output_schema root must be an object.");
        void Check(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var p in obj) { if (p.Key is "$ref" or "$defs") throw new ArgumentException("M8tes requires inline output_schema definitions."); Check(p.Value); }
            else if (node is JsonArray array) foreach (var child in array) Check(child);
        }
        Check(schema);
    }
    private static void ValidateRun(Conversation c, JsonNode run)
    {
        if (Str(run, "teammate_id") is { } agent && agent != c.AgentId.ToString(CultureInfo.InvariantCulture))
            throw new ArgumentException("M8tes run belongs to a different agent.");
        if (run.AsObject().ContainsKey("user_id") && Str(run, "user_id") != c.UserId)
            throw new ArgumentException("M8tes run belongs to a different end-user scope.");
    }
    private Task<JsonObject> GetRunAsync(Conversation c, CancellationToken ct)
        => ReadRunAsync(c, ct);
    private async Task<JsonObject> ReadRunAsync(Conversation c, CancellationToken ct)
        => Object(await SendJsonAsync(c, HttpMethod.Get, $"runs/{Id(c.RunId)}{ScopeQuery(c)}", null, ct));
    private static string ScopeQuery(Conversation c) => c.UserId is null ? "" : "?user_id=" + Uri.EscapeDataString(c.UserId);
    private static string Id(string id) => Uri.EscapeDataString(id);
    private static string? Str(JsonNode? node, string key) => node is JsonObject obj && obj[key] is JsonValue v ? v.ToString() : null;
    private static long Number(JsonNode? node, string key) => long.TryParse(Str(node, key), out var result) ? result : 0;
    private static JsonNode? Node(object? value) => value is JsonNode node ? node.DeepClone() : value is null ? null : JsonSerializer.SerializeToNode(value, Json);
    private static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Json);
    private static JsonObject Object(JsonNode? value) => value as JsonObject ?? throw new JsonException("M8tes response must be a JSON object.");
    private static IEnumerable<JsonObject> Rows(JsonNode? value) => (value as JsonArray ?? (value as JsonObject)?["data"] as JsonArray)?.OfType<JsonObject>() ?? [];
    private static Dictionary<string, object?> RawMetadata(object? raw) => new() { ["m8tes"] = new { raw = Element(raw) } };

    private HttpRequestMessage HttpRequest(Conversation c, HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.Key);
        return message;
    }
    private async Task<JsonNode?> SendJsonAsync(Conversation c, HttpMethod method, string path, JsonNode? payload, CancellationToken ct)
    {
        using var message = HttpRequest(c, method, path);
        if (payload is not null) message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"M8tes HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}", null, response.StatusCode);
    }
    private async Task<JsonNode?> SubmitAsync(Conversation c, string path, JsonObject payload,
        List<AIFileContentPart> attachments, string? idempotencyKey, CancellationToken ct)
    {
        using var message = HttpRequest(c, HttpMethod.Post, attachments.Count == 0 ? path : path + "/with-files");
        if (!string.IsNullOrWhiteSpace(idempotencyKey)) message.Headers.Add("Idempotency-Key", idempotencyKey);
        if (attachments.Count == 0) message.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        else
        {
            var multipart = new MultipartFormDataContent();
            message.Content = multipart;
            multipart.Add(new StringContent(payload.ToJsonString(), Encoding.UTF8), "payload");
            for (var i = 0; i < attachments.Count; i++)
            {
                var file = attachments[i];
                var bytes = DecodeFile(file);
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(file.MediaType ?? "application/octet-stream");
                multipart.Add(content, "files", SafeFilename(file.Filename ?? $"attachment-{i + 1}"));
            }
        }
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static byte[] DecodeFile(AIFileContentPart file)
    {
        if (file.Data is byte[] bytes) return bytes;
        var data = file.Data?.ToString() ?? throw new ArgumentException("M8tes input file has no data.");
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid M8tes base64 data URL.");
            data = data[(comma + 1)..];
        }
        if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            throw new NotSupportedException("M8tes input files require bytes, base64, or base64 data URLs; remote URLs are not downloaded.");
        return Convert.FromBase64String(data);
    }
    private static string SafeFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.Contains('/') || filename.Contains('\\') || filename is "." or "..")
            throw new ArgumentException("M8tes file name must be a basename.");
        return filename;
    }
    private async Task AddFilesAsync(Conversation c, List<AIContentPart> parts, CancellationToken ct)
    {
        var manifest = c.Raw["files"];
        if (manifest is null) manifest = await SendJsonAsync(c, HttpMethod.Get, $"runs/{Id(c.RunId)}/files", null, ct);
        c.Raw["files"] = manifest?.DeepClone();
        foreach (var file in Rows(manifest))
        {
            var name = SafeFilename(Str(file, "name") ?? throw new JsonException("M8tes file manifest omitted name."));
            using var message = HttpRequest(c, HttpMethod.Get, $"runs/{Id(c.RunId)}/files/{Id(name)}/download");
            using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccess(response, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var mime = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            parts.Add(new AIFileContentPart { Type = "file", Filename = name, MediaType = mime,
                Data = $"data:{mime};base64,{Convert.ToBase64String(bytes)}", Metadata = RawMetadata(file) });
        }
    }
}
