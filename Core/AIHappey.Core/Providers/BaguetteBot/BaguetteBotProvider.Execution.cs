using System.Net;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.BaguetteBot;

public sealed partial class BaguetteBotProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private sealed record Upload(string Path, byte[] Bytes);
    private sealed record Call(string AgentId, string? ModelId, string Operation, JsonElement Options,
        Dictionary<string, object?> Payload, IReadOnlyList<Upload> Files, int TimeoutSeconds, int PollIntervalMs)
    {
        public JsonElement? Agent { get; set; }
        public List<JsonElement> UploadedFiles { get; } = [];
        public Dictionary<string, object?> UnsupportedOptions { get; init; } = [];
    }
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
    {
        "agent_id", "operation", "after", "timeout_seconds", "poll_interval_ms", "download_paths", "live_view"
    };

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var call = BuildCall(request);
        using var timeout = CreateTimeout(call, cancellationToken);
        var ct = timeout.Token;
        if (call.Operation != "messages")
            return await ExecuteOperationAsync(request, call, ct);

        await ValidateAgentAsync(call, ct);
        await UploadFilesAsync(call, ct);
        var raw = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/messages", call.Payload, ct);
        ValidateRun(raw, call);
        if (Text(raw, "status") == "working" && (bool)call.Payload["wait"]!)
            raw = await WaitForCompletionAsync(call, raw, ct);
        return await MapRunAsync(request, call, raw, ct);
    }

    private static Call BuildCall(AIRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = GetOptions(request);
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("BaguetteBot provider metadata must be a JSON object.");
        foreach (var name in new[] { "agent_id", "operation", "after" })
            if (Property(options, name) is { ValueKind: not JsonValueKind.String })
                throw new ArgumentException($"BaguetteBot {name} must be a string.");
        var slug = request.Model ?? "";
        if (slug.StartsWith("baguettebot/", StringComparison.OrdinalIgnoreCase))
            slug = slug["baguettebot/".Length..];
        string? nativeModel = null;
        var selected = Text(options, "agent_id");
        string agentId;
        if (slug.StartsWith("agent/", StringComparison.Ordinal))
        {
            agentId = slug["agent/".Length..];
            if (selected is not null && selected != agentId)
                throw new ArgumentException("BaguetteBot agent_id conflicts with the selected agent slug.");
        }
        else if (slug.StartsWith("model/", StringComparison.Ordinal) && slug.Length > "model/".Length)
        {
            nativeModel = slug["model/".Length..];
            agentId = selected ?? throw new ArgumentException("A BaguetteBot model slug requires an existing agent_id.");
        }
        else
            throw new ArgumentException("Expected 'baguettebot/agent/{id}' or 'baguettebot/model/{nativeModelId}'.");
        ValidateAgentId(agentId);

        var operation = Text(options, "operation") ?? "messages";
        if (operation is not ("messages" or "stop" or "history" or "computer" or "live" or "download" or "screenshot"))
            throw new NotSupportedException($"BaguetteBot operation '{operation}' is not supported. Agent creation, updates and computer start/stop are not mapped.");
        if (!string.IsNullOrWhiteSpace(request.Instructions)
            || request.Input?.Items?.Any(item => item.Role is "system" or "developer") == true
            || request.Tools is { Count: > 0 } || request.ToolChoice is not null || request.ResponseFormat is not null)
            throw new NotSupportedException("BaguetteBot uses the existing agent's instructions, model and native tools; request-level chat controls are not supported.");
        var payload = options.EnumerateObject().Where(property => !Controls.Contains(property.Name))
            .ToDictionary(property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal);
        var files = new List<Upload>();
        if (operation == "messages")
        {
            var latest = request.Input?.Items?.LastOrDefault(item => item.Role == "user");
            if (latest?.Content?.Any(part => part is not (AITextContentPart or AIFileContentPart)) == true)
                throw new NotSupportedException("BaguetteBot messages accept text and inline files only.");
            var text = latest is not null
                ? string.Join("\n", latest.Content?.OfType<AITextContentPart>().Select(part => part.Text) ?? [])
                : request.Input?.Text;
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100_000)
                throw new ArgumentException("BaguetteBot requires a latest user task of 1-100000 characters.");
            payload["content"] = text;
            var wait = Property(options, "wait");
            if (wait.HasValue && wait.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("BaguetteBot wait must be a boolean.");
            payload["wait"] = wait?.ValueKind != JsonValueKind.False;
            if (!(bool)payload["wait"]! && Property(options, "download_paths") is not null)
                throw new ArgumentException("BaguetteBot asynchronous dispatch cannot download unfinished output files. Use operation=download explicitly later.");
            foreach (var file in latest?.Content?.OfType<AIFileContentPart>() ?? [])
                files.Add(ReadUpload(file));
        }
        ValidateDownloads(options);
        if (Property(options, "live_view") is { ValueKind: not JsonValueKind.Object })
            throw new ArgumentException("BaguetteBot live_view must be a native options object.");
        var timeout = PositiveInt(options, "timeout_seconds", 900);
        if (timeout > 86400)
            throw new ArgumentException("BaguetteBot timeout_seconds must not exceed 86400.");
        return new Call(agentId, nativeModel, operation, options, payload, files,
            timeout, PositiveInt(options, "poll_interval_ms", 1000))
        {
            // UI requests always carry temperature; Messages requires max_tokens.
            // Retain these unsupported controls without pretending the agent honors them.
            UnsupportedOptions = new Dictionary<string, object?>
            {
                ["temperature"] = request.Temperature, ["top_p"] = request.TopP,
                ["max_output_tokens"] = request.MaxOutputTokens, ["max_tool_calls"] = request.MaxToolCalls,
                ["parallel_tool_calls"] = request.ParallelToolCalls, ["verbosity"] = request.Verbosity
            }.Where(entry => entry.Value is not null).ToDictionary(entry => entry.Key, entry => entry.Value)
        };
    }

    private static JsonElement GetOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("baguettebot", out var options) == true && options is not null)
            return options is JsonElement element ? element.Clone() : JsonSerializer.SerializeToElement(options, Json);
        foreach (var key in new[] { "messages.request.raw", "chatcompletions.request.raw" })
        {
            if (request.Metadata?.TryGetValue(key, out var raw) != true || raw is not JsonElement json)
                continue;
            foreach (var envelope in new[] { "providerMetadata", "provider_metadata", "metadata" })
                if (Property(Property(json, envelope) ?? default, "baguettebot") is { } scoped)
                    return scoped.Clone();
        }
        return JsonSerializer.SerializeToElement(new { }, Json);
    }

    private static void ValidateAgentId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            throw new ArgumentException("BaguetteBot agent ID must be a single literal alphanumeric, hyphen or underscore segment.");
    }

    private static int PositiveInt(JsonElement options, string name, int fallback)
    {
        if (Property(options, name) is not { } value)
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
            throw new ArgumentException($"BaguetteBot {name} must be a positive integer.");
        return number;
    }

    private static CancellationTokenSource CreateTimeout(Call call, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(TimeSpan.FromSeconds(call.TimeoutSeconds));
        return source;
    }

    private static string AgentPath(Call call) => $"v1/agents/{Uri.EscapeDataString(call.AgentId)}";
    private static JsonElement? Property(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) ? value : null;
    private static string? Text(JsonElement root, string name)
        => Property(root, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
    private static string RequiredString(JsonElement root, string name)
        => !string.IsNullOrWhiteSpace(Text(root, name)) ? Text(root, name)! : throw new InvalidOperationException($"BaguetteBot response is missing {name}.");

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = CreateRequest(method, path, body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonSerializer.SerializeToElement(new { }, Json);
        var bodyText = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(bodyText) && (path.EndsWith("/stop", StringComparison.Ordinal)
            || method == HttpMethod.Post && path.EndsWith("/files", StringComparison.Ordinal)))
            return JsonSerializer.SerializeToElement(new { }, Json);
        using var document = JsonDocument.Parse(bodyText);
        var raw = document.RootElement.Clone();
        if (Property(raw, "error") is { ValueKind: not JsonValueKind.Null })
            throw new InvalidOperationException($"BaguetteBot native error: {raw.GetRawText()}");
        return raw;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var exception = new HttpRequestException($"BaguetteBot API ({(int)response.StatusCode}): {body}", null, response.StatusCode);
        if (response.Headers.RetryAfter is { } retry)
            exception.Data["Retry-After"] = retry.ToString();
        throw exception;
    }

    private async Task ValidateAgentAsync(Call call, CancellationToken ct)
    {
        var agent = await SendJsonAsync(HttpMethod.Get, AgentPath(call), null, ct);
        if (RequiredString(agent, "id") != call.AgentId)
            throw new InvalidOperationException("BaguetteBot returned a different agent ID.");
        if (call.ModelId is not null && Text(agent, "model") != call.ModelId)
            throw new ArgumentException("The selected BaguetteBot model does not match the agent's underlying model. Update the agent explicitly through the native API.");
        if (Text(agent, "status") == "working")
            throw new InvalidOperationException("BaguetteBot agent is already working. Follow its events/history or stop it explicitly; no task was submitted.");
        call.Agent = agent;
    }

    private static void ValidateRun(JsonElement run, Call call)
    {
        if (Text(run, "status") is not ("done" or "working"))
            throw new InvalidOperationException($"Unexpected BaguetteBot run: {run.GetRawText()}");
        if (Text(run, "agent_id") is { } id && id != call.AgentId)
            throw new InvalidOperationException("BaguetteBot run returned a different agent ID.");
        RequiredString(run, "message_id");
    }

    private async Task<JsonElement> WaitForCompletionAsync(Call call, JsonElement run, CancellationToken ct)
    {
        while (true)
        {
            var completed = await TryCompleteAsync(call, run, ct);
            if (completed.HasValue)
                return completed.Value;
            await Task.Delay(call.PollIntervalMs, ct);
        }
    }

    private async Task<JsonElement?> TryCompleteAsync(Call call, JsonElement run, CancellationToken ct)
    {
        var agent = await SendJsonAsync(HttpMethod.Get, AgentPath(call), null, ct);
        if (RequiredString(agent, "id") != call.AgentId)
            throw new InvalidOperationException("BaguetteBot returned a different agent ID.");
        call.Agent = agent;
        var status = RequiredString(agent, "status");
        if (status == "working")
            return null;
        if (status is not ("idle" or "blocked"))
            throw new InvalidOperationException($"Unexpected BaguetteBot agent status: {status}.");
        var messages = await ReadHistoryAsync(call, RequiredString(run, "message_id"), ct);
        var last = messages.LastOrDefault(message => Text(message, "role") == "assistant" && Text(message, "content") is not null);
        return JsonSerializer.SerializeToElement(new
        {
            @object = "run", agent_id = call.AgentId, message_id = RequiredString(run, "message_id"),
            status = status == "idle" ? "done" : "blocked",
            output = Text(last, "content"),
            messages, agent, accepted = run.Clone()
        }, Json);
    }

    private async Task<List<JsonElement>> ReadHistoryAsync(Call call, string? after, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var path = AgentPath(call) + "/messages?limit=200";
            if (after is not null)
                path += "&after=" + Uri.EscapeDataString(after);
            var root = await SendJsonAsync(HttpMethod.Get, path, null, ct);
            var page = ReadList(root, "messages");
            foreach (var item in page)
            {
                var id = RequiredString(item, "id");
                if (!seen.Add(id))
                    throw new InvalidOperationException("BaguetteBot conversation pagination did not advance.");
                result.Add(item);
            }
            if (page.Count < 200)
                return result;
            after = RequiredString(page[^1], "id");
        }
    }

    private static Dictionary<string, object?> Metadata(Call call, JsonElement raw) => new()
    {
        ["baguettebot"] = new { agent_id = call.AgentId, model_id = call.ModelId ?? Text(call.Agent ?? default, "model"), message_id = Text(raw, "message_id"), raw = raw.Clone() },
        ["baguettebot.agent_id"] = call.AgentId,
        ["baguettebot.model_id"] = call.ModelId ?? Text(call.Agent ?? default, "model"),
        ["baguettebot.message_id"] = Text(raw, "message_id"),
        ["baguettebot.status"] = Text(raw, "status"),
        ["baguettebot.unsupported_options"] = call.UnsupportedOptions,
        ["baguettebot.raw"] = raw.Clone()
    };

    private async Task<AIResponse> MapRunAsync(AIRequest request, Call call, JsonElement raw, CancellationToken ct)
    {
        var content = new List<AIContentPart>();
        var nativeMessages = Property(raw, "messages") is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(message => message.Clone()).ToList() : [];
        var last = nativeMessages.LastOrDefault(message => Text(message, "role") == "assistant" && Text(message, "content") is not null);
        if ((Text(raw, "output") ?? Text(last, "content")) is { } text)
            content.Add(new AITextContentPart { Type = "text", Text = text, Metadata = Metadata(call, raw) });
        foreach (var message in nativeMessages.Where(message => Text(message, "role") == "tool"))
        {
            var failed = Property(Property(message, "tool") ?? default, "ok")?.ValueKind == JsonValueKind.False;
            content.Add(new AIToolCallContentPart
            {
                Type = "tool-call",
                ToolCallId = RequiredString(message, "id"), ToolName = Text(Property(message, "tool") ?? default, "name"),
                ProviderExecuted = true, State = failed ? "output-error" : "output-available",
                Output = new CallToolResult
                {
                    IsError = failed, StructuredContent = message.Clone(),
                    Content = Text(message, "content") is { } toolText ? [new TextContentBlock { Text = toolText }] : []
                },
                Metadata = Metadata(call, message)
            });
        }
        var metadata = Metadata(call, raw);
        metadata["baguettebot.output_message_id"] = Text(last, "id");
        metadata["baguettebot.agent"] = call.Agent;
        metadata["baguettebot.uploads"] = call.UploadedFiles;
        metadata["baguettebot.request"] = JsonSerializer.SerializeToElement(call.Payload, Json);
        await AddExtrasAsync(call, content, metadata, ct);
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = Text(raw, "status") switch
            {
                "done" => "completed", "working" => "in_progress", "blocked" => "failed",
                _ => throw new InvalidOperationException($"Unexpected BaguetteBot run: {raw.GetRawText()}")
            },
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = metadata }], Metadata = metadata },
            Metadata = metadata
        };
    }

    private async Task<AIResponse> ExecuteOperationAsync(AIRequest request, Call call, CancellationToken ct)
    {
        JsonElement raw;
        var content = new List<AIContentPart>();
        switch (call.Operation)
        {
            case "stop":
                raw = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/stop", null, ct);
                break;
            case "history":
                raw = JsonSerializer.SerializeToElement(new { @object = "list", data = await ReadHistoryAsync(call, Text(call.Options, "after"), ct) }, Json);
                break;
            case "computer":
                raw = await SendJsonAsync(HttpMethod.Get, AgentPath(call) + "/computer", null, ct);
                break;
            case "live":
                raw = await SendJsonAsync(HttpMethod.Post, AgentPath(call) + "/computer/live", Property(call.Options, "live_view") ?? JsonSerializer.SerializeToElement(new { }, Json), ct);
                break;
            case "download":
                if (Property(call.Options, "download_paths") is null)
                    throw new ArgumentException("BaguetteBot download requires download_paths.");
                raw = JsonSerializer.SerializeToElement(new { agent_id = call.AgentId, paths = Property(call.Options, "download_paths") }, Json);
                await DownloadFilesAsync(call, content, ct);
                break;
            default:
                var screenshot = await DownloadAsync(AgentPath(call) + "/computer/screenshot", "screen.jpg", ct);
                content.Add(screenshot);
                raw = JsonSerializer.SerializeToElement(new { agent_id = call.AgentId, media_type = screenshot.MediaType }, Json);
                break;
        }
        content.Add(new AIToolCallContentPart
        {
            Type = "tool-call",
            ToolCallId = request.Id ?? $"baguettebot-{Guid.NewGuid():N}", ToolName = $"baguettebot_{call.Operation}",
            Input = call.Options.Clone(), Output = new CallToolResult { StructuredContent = raw.Clone(), Content = [] },
            ProviderExecuted = true, State = "output-available", Metadata = Metadata(call, raw)
        });
        return new AIResponse
        {
            ProviderId = GetIdentifier(), Model = request.Model, Status = "completed", Metadata = Metadata(call, raw),
            Output = new AIOutput { Items = [new AIOutputItem { Role = "assistant", Content = content, Metadata = Metadata(call, raw) }] }
        };
    }
}
