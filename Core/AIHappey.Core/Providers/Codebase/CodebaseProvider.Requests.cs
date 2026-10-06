using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Codebase;

public partial class CodebaseProvider
{
    private const string ExecutorTool = "codebase_build_executor";
    private const string ReceiptType = "aihappey.codebase.build.v1";
    private sealed record State(string ProjectId, string SessionId);
    private sealed record Prepared(AIRequest Request, string Model, string Operation, State? Previous,
        Dictionary<string, object?> Body, Dictionary<string, string> Headers, string Key, double WaitSeconds, double PollSeconds);

    private static State? FindReceipt(object? value, int depth = 0)
    {
        if (value is null || depth > 10) return null;
        var json = Element(value);
        if (json.ValueKind == JsonValueKind.String)
        {
            try { using var parsed = JsonDocument.Parse(json.GetString()!); return FindReceipt(parsed.RootElement, depth + 1); }
            catch (JsonException) { return null; }
        }
        if (json.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in json.EnumerateArray().Reverse())
                if (FindReceipt(child, depth + 1) is { } result) return result;
            return null;
        }
        if (json.ValueKind != JsonValueKind.Object) return null;
        if (Text(json, "type") == ReceiptType && Text(json, "provider") == "codebase")
        {
            var project = Text(json, "projectId");
            var session = Text(json, "sessionId");
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(session))
                throw new ArgumentException("Codebase executor receipt must contain both projectId and sessionId.");
            return new(project, session);
        }
        foreach (var name in new[] { "structuredContent", "structured_content", "codebase", "receipt", "output", "content", "text", "raw" })
            if (Property(json, name) is { } nested && FindReceipt(nested, depth + 1) is { } result) return result;
        return null;
    }

    private static State? Restore(AIRequest request)
    {
        // Search newest receipts first; do not accept unrelated provider session fields.
        foreach (var item in (request.Input?.Items ?? []).AsEnumerable().Reverse())
        {
            foreach (var tool in (item.Content ?? []).OfType<AIToolCallContentPart>().Reverse())
            {
                var state = FindReceipt(tool.Output) ?? FindReceipt(tool.Metadata);
                if (state is not null) return state;
                if (tool.ToolName == ExecutorTool && FindReceipt(tool.Input) is { } input) return input;
            }
            if (FindReceipt(item.Metadata) is { } metadata) return metadata;
        }
        return FindReceipt(request.Input?.Metadata) ?? FindReceipt(request.Metadata);
    }

    private static Dictionary<string, object?> Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("codebase", out var value) != true || value is null) return [];
        var json = Element(value);
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("Codebase provider metadata must be an object.");
        return json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static double Control(JsonElement controls, string name, double fallback, double max)
    {
        if (Property(controls, name) is not { } value) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number <= 0 || number > max)
            throw new ArgumentException($"Codebase gateway.{name} must be greater than zero and at most {max}.");
        return number;
    }

    private static Prepared Prepare(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var local = request.Model ?? "";
        if (local.StartsWith("codebase/", StringComparison.OrdinalIgnoreCase)) local = local[9..];
        if (!local.StartsWith("build/", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(local[6..]))
            throw new ArgumentException("Codebase requires codebase/build/auto or codebase/build/{modelId}.");
        var model = local[6..];
        if (request.Tools?.Count > 0) throw new NotSupportedException("Codebase builds do not support gateway client tools.");
        if (request.ResponseFormat is not null) throw new NotSupportedException("Codebase builds do not support a gateway response format.");
        var previous = Restore(request);
        var options = Options(request);
        var controls = options.Remove("gateway", out var gateway) ? Element(gateway) : Element(new { });
        if (controls.ValueKind != JsonValueKind.Object) throw new ArgumentException("Codebase gateway controls must be an object.");
        var operation = Text(controls, "operation") ?? "build";
        if (operation is not ("build" or "retry" or "status")) throw new ArgumentException("Codebase gateway.operation must be build, retry, or status.");
        if (operation != "build" && previous is null) throw new ArgumentException("Codebase retry/status requires a prior executor receipt.");
        var wait = Control(controls, "wait_seconds", 1800, 86400);
        var poll = Control(controls, "poll_seconds", 2, 30);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.Remove("headers", out var supplied))
        {
            var json = Element(supplied);
            if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("Codebase headers must be an object.");
            foreach (var pair in json.EnumerateObject())
            {
                if (pair.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Codebase header values must be strings.");
                headers[pair.Name] = pair.Value.GetString()!;
            }
        }
        var key = options.Remove("idempotency_key", out var suppliedKey) ? Element(suppliedKey).GetString() : null;
        key ??= headers.GetValueOrDefault("Idempotency-Key");
        key ??= request.Headers?.FirstOrDefault(p => p.Key.Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase)).Value;
        key ??= Guid.NewGuid().ToString("N");
        if (key.Length is < 1 or > 255 || key.Any(c => c < '!' || c > '~'))
            throw new ArgumentException("Codebase Idempotency-Key must contain 1-255 printable ASCII characters without spaces.");

        // Raw body pass-through except gateway controls and state: restored state is authoritative.
        foreach (var name in new[] { "sessionId", "receipt", "type", "provider", "raw", "accepted", "status", "responseHeaders", "operation", "idempotencyKey" })
            options.Remove(name);
        if (previous is not null)
        {
            if (options.TryGetValue("projectId", out var project) && Element(project).GetString() != previous.ProjectId)
                throw new ArgumentException("Codebase projectId conflicts with the latest executor receipt.");
            options["projectId"] = previous.ProjectId;
        }
        if (options.TryGetValue("model", out var suppliedModel) && Element(suppliedModel).GetString() != model)
            throw new ArgumentException("Codebase metadata model conflicts with the selected build model.");
        options["model"] = model;
        options.Remove("prompt"); // Never replace the user's new turn with a stale metadata prompt.
        if (operation != "status")
        {
            var items = request.Input?.Items ?? [];
            var boundary = previous is null ? -1 : items.FindLastIndex(i => i.Role is not ("user" or "system" or "developer"));
            var selected = items.Skip(boundary + 1).Where(i => i.Role is "user" or "system" or "developer").ToArray();
            if (selected.SelectMany(i => i.Content ?? []).Any(p => p is not AITextContentPart))
                throw new NotSupportedException("Codebase builds accept text instructions only; attachments are not documented.");
            var texts = selected.SelectMany(i => i.Content ?? []).OfType<AITextContentPart>().Select(p => p.Text).ToList();
            if (items.Count == 0 && !string.IsNullOrWhiteSpace(request.Input?.Text)) texts.Add(request.Input.Text);
            if (!string.IsNullOrWhiteSpace(request.Instructions)) texts.Insert(0, request.Instructions);
            var prompt = string.Join("\n\n", texts.Where(t => !string.IsNullOrWhiteSpace(t)));
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Codebase requires new text instructions after the latest assistant/tool turn.");
            options["prompt"] = prompt;
        }
        if (operation == "retry")
        {
            var unknown = options.Keys.Where(k => k is not ("prompt" or "model" or "projectId")).ToArray();
            if (unknown.Length > 0) throw new ArgumentException("Codebase retry only supports replacement prompt; remove build options: " + string.Join(", ", unknown));
            options = new() { ["prompt"] = options["prompt"] };
        }
        return new(request, "codebase/" + local, operation, previous, options, headers, key, wait, poll);
    }
}
