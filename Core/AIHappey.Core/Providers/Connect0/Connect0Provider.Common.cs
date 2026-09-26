using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Connect0;

public sealed partial class Connect0Provider
{
    private const string IdentityTool = "connect0_agent_run";
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly Regex AccountSlug = new("^[a-z][a-z0-9-]{1,28}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex AgentSlug = new("^[a-z][a-z0-9-]{0,38}[a-z0-9]$", RegexOptions.Compiled);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromSeconds(90);

    private enum RouteKind { ListAgents, Agent, AgentDetail, ListRuns, RunDetail, CancelRun, ReplayRun, StreamRun, ListThreads, Messages }
    private sealed record Route(RouteKind Kind, string Account, string? Agent, string Model);
    private sealed record Reply(JsonElement Raw, HttpStatusCode Status, Dictionary<string, object?> Headers);

    private static Route ParseRoute(string? model)
    {
        var local = model?.Trim() ?? "";
        if (local.StartsWith("connect0/", StringComparison.OrdinalIgnoreCase)) local = local[9..];
        var parts = local.Split('/');
        if (parts.Length < 2 || !AccountSlug.IsMatch(parts[0]) || parts[1] != "agents")
            throw new ArgumentException($"Unknown Connect0 model '{model}'. Expected an account-scoped agent model.", nameof(model));
        var kind = parts.Length switch
        {
            2 => RouteKind.ListAgents,
            3 when AgentSlug.IsMatch(parts[2]) => RouteKind.Agent,
            4 when AgentSlug.IsMatch(parts[2]) && parts[3] == "detail" => RouteKind.AgentDetail,
            4 when AgentSlug.IsMatch(parts[2]) && parts[3] == "runs" => RouteKind.ListRuns,
            4 when AgentSlug.IsMatch(parts[2]) && parts[3] == "threads" => RouteKind.ListThreads,
            5 when AgentSlug.IsMatch(parts[2]) && parts[3] == "runs" => parts[4] switch
            {
                "detail" => RouteKind.RunDetail, "cancel" => RouteKind.CancelRun,
                "replay" => RouteKind.ReplayRun, "stream" => RouteKind.StreamRun,
                _ => throw new ArgumentException($"Unknown Connect0 operation '{model}'.", nameof(model))
            },
            5 when AgentSlug.IsMatch(parts[2]) && parts[3] == "threads" && parts[4] == "messages" => RouteKind.Messages,
            _ => throw new ArgumentException($"Unknown Connect0 operation '{model}'.", nameof(model))
        };
        return new Route(kind, parts[0], parts.Length > 2 ? parts[2] : null, $"connect0/{local}");
    }

    private static JsonElement Element(object? value) => value is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(value, Json);
    private static JsonElement? Property(JsonElement? value, string key)
        => value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(key, out var property) ? property.Clone() : null;
    private static string? String(JsonElement? value, string key)
        => Property(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;
    private static string? Option(JsonElement options, params string[] keys)
        => keys.Select(key => String(options, key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static JsonElement Options(AIRequest request)
    {
        if (request.Metadata?.TryGetValue("connect0", out var scoped) == true && scoped is not null
            && Element(scoped) is { ValueKind: JsonValueKind.Object } direct) return direct;
        // Chat completions keeps the original metadata inside chatcompletions.request.metadata.
        if (request.Metadata?.TryGetValue("chatcompletions.request.metadata", out var chatMetadata) == true
            && Property(Element(chatMetadata), "connect0") is { ValueKind: JsonValueKind.Object } chat) return chat;
        return JsonSerializer.SerializeToElement(new { }, Json);
    }

    private static string? LatestUserText(AIRequest request)
        => request.Input?.Items?.AsEnumerable().Reverse()
            .Where(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(item => string.Join("\n", item.Content?.OfType<AITextContentPart>().Select(text => text.Text) ?? []))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? request.Input?.Text;

    private static JsonElement Input(AIRequest request, JsonElement options)
    {
        var text = LatestUserText(request);
        if (!string.IsNullOrWhiteSpace(text))
            try
            {
                using var parsed = JsonDocument.Parse(text);
                if (parsed.RootElement.ValueKind == JsonValueKind.Object) return parsed.RootElement.Clone();
            }
            catch (JsonException) { /* Fall back to the provider input, never invent a message/prompt key. */ }
        return Property(options, "input") is { ValueKind: JsonValueKind.Object } configured
            ? configured : JsonSerializer.SerializeToElement(new { }, Json);
    }

    private static string? PreviousIdentity(AIRequest request, Route route, string key)
    {
        foreach (var item in request.Input?.Items?.AsEnumerable().Reverse() ?? [])
        {
            foreach (var part in item.Content?.OfType<AIToolCallContentPart>().Reverse() ?? [])
            {
                if (part.ToolName != IdentityTool || part.ProviderExecuted != true || part.Output is null) continue;
                var result = Element(part.Output);
                var identity = Property(result, "structuredContent") ?? result;
                if (String(identity, "account_slug") == route.Account && String(identity, "agent_slug") == route.Agent)
                    if (String(identity, key) is { Length: > 0 } id) return id;
            }
            // Responses function_call_output pairs may be replayed as plain tool results.
            if (item.Type == "function_call_output" && item.Metadata?.TryGetValue("call_id", out var callId) == true
                && callId?.ToString()?.StartsWith("connect0-run-", StringComparison.Ordinal) == true)
                foreach (var part in item.Content?.OfType<AIToolCallContentPart>() ?? [])
                {
                    if (part.Output is null) continue;
                    var identity = Element(part.Output);
                    if (String(identity, "account_slug") == route.Account && String(identity, "agent_slug") == route.Agent)
                        if (String(identity, key) is { Length: > 0 } id) return id;
                }
        }
        return null;
    }

    private static string RequiredId(AIRequest request, JsonElement options, Route route, string key)
    {
        var camel = key switch { "run_id" => "runId", "thread_id" => "threadId", _ => key };
        var value = Option(options, key, camel) ?? PreviousIdentity(request, route, key);
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Connect0 {key} is required in provider metadata or a previous Connect0 run receipt.");
        if (!Guid.TryParse(value, out _)) throw new ArgumentException($"Connect0 {key} must be a UUID.");
        return value;
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);
    private static string AgentPath(Route route) => $"v1/accounts/{Enc(route.Account)}/agents/{Enc(route.Agent!)}";
    private static string PublicAgentPath(Route route) => $"v1/agents/{Enc(route.Account)}/{Enc(route.Agent!)}";
    private static string Query(JsonElement options, params string[] keys)
    {
        var args = new List<string>();
        foreach (var key in keys)
            if (Property(options, key) is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null) } value)
                args.Add($"{key}={Enc(value.ToString())}");
        return args.Count == 0 ? "" : "?" + string.Join("&", args);
    }

    private static Dictionary<string, object?> Headers(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "RateLimit-Limit", "RateLimit-Remaining", "RateLimit-Reset", "Retry-After", "X-Request-Id" })
            if (response.Headers.TryGetValues(key, out var values)) headers[key] = string.Join(",", values);
        return headers;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var code = "upstream_error";
        var message = $"Connect0 HTTP {(int)response.StatusCode}";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var error = Property(doc.RootElement, "error");
            code = String(error, "code") ?? code;
            message = String(error, "message") ?? message;
        }
        catch (JsonException) { }
        var retry = response.Headers.RetryAfter?.ToString();
        var exception = new HttpRequestException($"Connect0 {code}: {message}" + (retry is null ? "" : $" (Retry-After: {retry})"), null, response.StatusCode);
        if (response.Headers.RetryAfter?.Delta is { } delay) exception.Data["retryAfter"] = delay;
        throw exception;
    }

    private async Task<Reply> SendJson(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = CreateRequest(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        var raw = response.StatusCode == HttpStatusCode.NoContent
            ? JsonSerializer.SerializeToElement(new { }, Json)
            : JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        return new Reply(raw, response.StatusCode, Headers(response));
    }

    private static Dictionary<string, object?> Metadata(Route route, Reply reply, string? runId = null, string? threadId = null)
    {
        var raw = reply.Raw;
        return new Dictionary<string, object?>
        {
            ["connect0"] = new Dictionary<string, object?>
            {
                ["account_slug"] = route.Account, ["agent_slug"] = route.Agent,
                ["agent_id"] = String(raw, "agent_id") ?? String(raw, "id"),
                ["run_id"] = runId ?? String(raw, "id"), ["thread_id"] = threadId ?? String(raw, "thread_id"),
                ["status"] = String(raw, "status"), ["http_status"] = (int)reply.Status,
                ["raw"] = raw.Clone(), ["headers"] = reply.Headers
            }
        };
    }

    private static AIToolCallContentPart Receipt(Route route, Reply reply, string operation, string? runId, string? threadId)
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            account_slug = route.Account, agent_slug = route.Agent, agent_id = String(reply.Raw, "agent_id"),
            run_id = runId, thread_id = threadId, status = String(reply.Raw, "status"),
            operation, raw = reply.Raw.Clone()
        }, Json);
        return new AIToolCallContentPart
        {
            Type = "tool-call", ToolName = IdentityTool, Title = $"Connect0 {operation}",
            ToolCallId = $"connect0-run-{runId ?? Guid.NewGuid().ToString("N")}",
            ProviderExecuted = true, State = "output-available",
            Input = new { account_slug = route.Account, agent_slug = route.Agent, operation },
            Output = new CallToolResult { StructuredContent = output },
            Metadata = Metadata(route, reply, runId, threadId)
        };
    }
}
