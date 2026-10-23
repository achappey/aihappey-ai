using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.HumanBrowser;

public partial class HumanBrowserProvider
{
    private const string ModelId = "humanbrowser/browser-task";
    private const string SessionToolName = "humanbrowser_session";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private sealed record PreparedTurn(JsonObject Parameters, bool Resume, JsonObject? PausedTask = null, bool Declined = false);

    private async Task<PreparedTurn> PrepareAsync(AIRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Model is not (null or "browser-task" or ModelId))
            throw new ArgumentException($"HumanBrowser supports only {ModelId}.", nameof(request));

        // The provider scope is the native A2A params object, not a gateway settings schema.
        var parameters = Options(request);
        var message = parameters["message"] as JsonObject ?? new JsonObject();
        var receipt = FindReceipt(request);
        var answer = FindFormAnswer(request);
        var resume = false;
        if (answer is not null && Text(receipt, "state") != "input-required")
            throw new InvalidOperationException("HumanBrowser form answer has no matching paused-task receipt.");
        if (Text(receipt, "state") == "input-required")
        {
            var taskId = Text(receipt, "taskId") ?? throw new InvalidOperationException("HumanBrowser continuation has no task ID.");
            var task = await RpcAsync("tasks/get", new JsonObject { ["id"] = taskId }, ct);
            if (Text(task["status"], "state") != "input-required")
                throw new InvalidOperationException("HumanBrowser task is no longer waiting for input. Start a new task explicitly with full context; the previous goal was not resubmitted.");
            var pendingId = PendingInputId(task);
            if (pendingId is null || (Text(receipt, "in_reply_to") is { } previous && pendingId != previous))
                throw new InvalidOperationException("HumanBrowser pending input changed or has no request ID. Refresh the task before answering.");
            if (answer is not null)
            {
                if (answer.ToolCallId != FormId(taskId, pendingId))
                    throw new InvalidOperationException("HumanBrowser form answer is stale or belongs to another task.");
                var result = ElicitationResult(answer.Output);
                if (Text(result, "action") is "cancel" or "decline")
                    return new PreparedTurn(parameters, false, task, true);
                if (Text(result, "action") != "accept" || result?["content"] is not JsonObject content
                    || Text(content, "answer") is not { } text || text.Length > 20000
                    || content.Any(p => p.Key is not ("answer" or "sensitive")))
                    throw new ArgumentException("HumanBrowser requires an accepted elicitation result with a non-empty answer (at most 20000 characters).");
                if (content["sensitive"] is { } sensitivity && (sensitivity is not JsonValue value || !value.TryGetValue<bool>(out _)))
                    throw new ArgumentException("HumanBrowser elicitation sensitive must be a boolean.");
                // Never mix a form answer with client-supplied native parts. It must answer
                // this exact challenge, not execute an unrelated payload or old goal.
                message["parts"] = content["sensitive"]?.GetValue<bool>() == true
                    ? new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "Answer the pending question using <humanbrowser_answer>." },
                        new JsonObject { ["kind"] = "data", ["data"] = new JsonObject { ["humanbrowser_answer"] = text }, ["metadata"] = new JsonObject { ["sensitive"] = true } })
                    : new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = text });
            }
            message["referenceTaskIds"] ??= new JsonArray(taskId);
            if (message["referenceTaskIds"] is not JsonArray references || references.Count != 1 || references[0]?.GetValue<string>() != taskId)
                throw new ArgumentException("HumanBrowser resume must reference the paused receipt's task.");
            var metadata = message["metadata"] as JsonObject ?? new JsonObject();
            metadata["in_reply_to"] ??= pendingId;
            if (Text(metadata, "in_reply_to") != pendingId)
                throw new ArgumentException("HumanBrowser in_reply_to does not match the current pending input.");
            message["metadata"] = metadata.DeepClone();
            message["contextId"] ??= task["contextId"]?.DeepClone() ?? receipt?["contextId"]?.DeepClone();
            resume = true;
        }
        // Explicit native resume parameters remain client controlled.
        resume |= message["referenceTaskIds"] is JsonArray { Count: > 0 };
        if (Text(receipt, "profile") is { } profile && parameters["metadata"]?["profile"] is null)
        {
            var metadata = message["metadata"] as JsonObject ?? new JsonObject();
            metadata["profile"] ??= profile;
            message["metadata"] = metadata.DeepClone();
        }

        message["role"] ??= "user";
        message["messageId"] ??= Guid.NewGuid().ToString("N");
        if (message["parts"] is null)
        {
            var parts = new JsonArray();
            var items = request.Input?.Items ?? [];
            var latest = items.FindLastIndex(i => i.Role == "user" && (i.Content ?? []).Any(p => p is AITextContentPart or AIFileContentPart));
            var texts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Instructions)) texts.Add(request.Instructions);
            if (resume)
            {
                if (latest >= 0) texts.AddRange((items[latest].Content ?? []).OfType<AITextContentPart>().Select(p => p.Text));
            }
            else
            {
                // Only conversational text is replayed, never synthetic tools or native DataParts.
                foreach (var item in items)
                {
                    var text = string.Join("\n", (item.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text));
                    if (!string.IsNullOrWhiteSpace(text)) texts.Add($"{item.Role ?? "user"}: {text}");
                }
            }
            if (!string.IsNullOrWhiteSpace(request.Input?.Text)) texts.Add(request.Input.Text);
            var goal = string.Join("\n\n", texts);
            if (!string.IsNullOrWhiteSpace(goal)) parts.Add(new JsonObject { ["kind"] = "text", ["text"] = goal });
            if (latest >= 0)
                foreach (var file in (items[latest].Content ?? []).OfType<AIFileContentPart>())
                    parts.Add(FilePart(file));
            message["parts"] = parts;
        }
        if (message["parts"] is not JsonArray { Count: > 0 })
            throw new ArgumentException("HumanBrowser requires text, files, or native message.parts.", nameof(request));
        parameters["message"] = message.DeepClone();
        return new PreparedTurn(parameters, resume);
    }

    private static JsonObject Options(AIRequest request)
    {
        var metadata = JsonSerializer.SerializeToNode(request.Metadata, Json) as JsonObject;
        if (metadata?["humanbrowser"] is JsonObject direct) return (JsonObject)direct.DeepClone();
        foreach (var key in new[] { "chatcompletions.request.raw", "messages.request.raw" })
            if (metadata?[key] is JsonObject raw)
            {
                foreach (var container in new[] { "providerMetadata", "provider_metadata", "metadata" })
                    if (raw[container]?["humanbrowser"] is JsonObject scope) return (JsonObject)scope.DeepClone();
            }
        return new JsonObject();
    }

    private static JsonObject FilePart(AIFileContentPart file)
    {
        var descriptor = new JsonObject { ["name"] = file.Filename, ["mimeType"] = file.MediaType };
        if (file.Data is byte[] bytes) descriptor["bytes"] = Convert.ToBase64String(bytes);
        else
        {
            var data = file.Data is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : file.Data as string;
            if (data is null) throw new ArgumentException("HumanBrowser files require a public HTTP(S) URL, data URI, or bytes.");
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = data.IndexOf(',');
                if (comma < 0 || !data[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("HumanBrowser file data URIs must be base64 encoded.");
                descriptor["mimeType"] ??= data[5..data.IndexOf(';')];
                descriptor["bytes"] = data[(comma + 1)..];
            }
            else if (Uri.TryCreate(data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") descriptor["uri"] = data;
            else throw new ArgumentException("HumanBrowser cannot read local files; supply a public HTTP(S) URL or bytes.");
        }
        return new JsonObject { ["kind"] = "file", ["file"] = descriptor };
    }

    private static JsonObject? FindReceipt(AIRequest request)
    {
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                if (tool.ToolName != SessionToolName && !tool.ToolCallId.StartsWith("humanbrowser-session-", StringComparison.Ordinal)) continue;
                foreach (var value in new[] { tool.Output, tool.Input, tool.Metadata })
                    if (ExtractReceipt(JsonSerializer.SerializeToNode(value, Json)) is { } receipt) return receipt;
            }
        return null;
    }

    private static JsonObject? ExtractReceipt(JsonNode? node, int depth = 0)
    {
        if (depth > 12) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            try { return ExtractReceipt(JsonNode.Parse(text), depth + 1); }
            catch (JsonException) { return null; }
        }
        if (node is JsonArray array)
            foreach (var child in array)
                if (ExtractReceipt(child, depth + 1) is { } receipt) return receipt;
        if (node is not JsonObject obj) return null;
        if (Text(obj, "type") == SessionToolName && Text(obj, "model") == ModelId) return (JsonObject)obj.DeepClone();
        foreach (var key in new[] { "structuredContent", "output", "content", "text", "humanbrowser" })
            if (ExtractReceipt(obj[key], depth + 1) is { } receipt) return receipt;
        return null;
    }

    private static string? PendingInputId(JsonNode? task)
        => Text(task?["metadata"]?["input_required"], "req_id")
            ?? Text(task?["metadata"]?["input_required"], "request_id")
            ?? Text(task?["metadata"]?["input_required"], "id")
            ?? Text(task?["metadata"]?["input_required"]?["resume_example"]?["params"]?["message"]?["metadata"], "in_reply_to");

    private static string? Text(JsonNode? node, string key)
        => node is JsonObject obj && obj[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            if (target[key] is JsonObject old && value is JsonObject next) Merge(old, next);
            else target[key] = value?.DeepClone();
    }
}
