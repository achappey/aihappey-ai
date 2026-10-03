using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cursor;

/// <summary>A credential-frozen Cloud Agents client. Bodies and queries retain unknown API fields.</summary>
public sealed class CursorApiClient
{
    private const string Host = "https://api.cursor.com";
    private readonly HttpClient _http;
    private readonly string _key;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> RepositoryRequests = new();

    public CursorApiClient(HttpClient httpClient, string apiKey)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _key = apiKey;
    }

    private static string Id(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }
    private static string Agent(string id) => "/v1/agents/" + Id(id);
    private static string Run(string agent, string run) => Agent(agent) + "/runs/" + Id(run);
    private Task<JsonElement> Get(string path, object? query, CancellationToken ct) => Send(HttpMethod.Get, path, null, query, ct);
    private Task<JsonElement> Post(string path, object? body, CancellationToken ct) => Send(HttpMethod.Post, path, body, null, ct);

    public Task<JsonElement> CreateAgentAsync(object body, CancellationToken ct = default) => Post("/v1/agents", body, ct);
    public Task<JsonElement> ListAgentsAsync(object? query = null, CancellationToken ct = default) => Get("/v1/agents", query ?? new { includeArchived = false }, ct);
    public Task<JsonElement> GetAgentAsync(string agentId, CancellationToken ct = default) => Get(Agent(agentId), null, ct);
    public Task<JsonElement> CreateRunAsync(string agentId, object body, CancellationToken ct = default) => Post(Agent(agentId) + "/runs", body, ct);
    public Task<JsonElement> ListRunsAsync(string agentId, object? query = null, CancellationToken ct = default) => Get(Agent(agentId) + "/runs", query, ct);
    public Task<JsonElement> GetRunAsync(string agentId, string runId, CancellationToken ct = default) => Get(Run(agentId, runId), null, ct);
    public IAsyncEnumerable<CursorSseEvent> StreamRunAsync(string agentId, string runId, string? lastEventId = null, object? query = null, CancellationToken ct = default)
        => Stream(Run(agentId, runId) + "/stream", query, lastEventId, ct);
    public Task<JsonElement> CancelRunAsync(string agentId, string runId, CancellationToken ct = default) => Post(Run(agentId, runId) + "/cancel", null, ct);
    public Task<JsonElement> GetUsageAsync(string agentId, object? query = null, CancellationToken ct = default) => Get(Agent(agentId) + "/usage", query, ct);
    public Task<JsonElement> ListArtifactsAsync(string agentId, object? query = null, CancellationToken ct = default) => Get(Agent(agentId) + "/artifacts", query, ct);
    public Task<JsonElement> DownloadArtifactAsync(string agentId, object query, CancellationToken ct = default) => Get(Agent(agentId) + "/artifacts/download", query, ct);
    public Task<JsonElement> ArchiveAgentAsync(string agentId, CancellationToken ct = default) => Post(Agent(agentId) + "/archive", null, ct);
    public Task<JsonElement> UnarchiveAgentAsync(string agentId, CancellationToken ct = default) => Post(Agent(agentId) + "/unarchive", null, ct);
    public Task<JsonElement> DeleteAgentAsync(string agentId, CancellationToken ct = default) => Send(HttpMethod.Delete, Agent(agentId), null, null, ct);
    public Task<JsonElement> CreateSubTokenAsync(object body, CancellationToken ct = default) => Post("/v1/sub-tokens", body, ct);
    public Task<JsonElement> GetMeAsync(CancellationToken ct = default) => Get("/v1/me", null, ct);
    public Task<JsonElement> ListModelsAsync(CancellationToken ct = default) => Get("/v1/models", null, ct);

    // This exceptionally restricted endpoint is never used by model discovery. No automatic retry.
    public Task<JsonElement> ListRepositoriesAsync(CancellationToken ct = default)
    {
        var bucket = RepositoryRequests.GetOrAdd(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_key))), _ => new());
        lock (bucket)
        {
            var now = DateTimeOffset.UtcNow;
            while (bucket.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromHours(1)) bucket.Dequeue();
            if (bucket.Count >= 30 || bucket.Count > 0 && now - bucket.Last() < TimeSpan.FromMinutes(1))
                throw new InvalidOperationException("Cursor repositories rate limit: at most one request/minute and thirty/hour per credential.");
            bucket.Enqueue(now);
        }
        return Send(HttpMethod.Get, "/v1/repositories", null, null, ct, retry: false);
    }

    public Task<JsonElement> ListPrivateWorkersAsync(object? query = null, CancellationToken ct = default) => Get("/v0/private-workers", query, ct);
    public Task<JsonElement> GetPrivateWorkersSummaryAsync(object? query = null, CancellationToken ct = default) => Get("/v0/private-workers/summary", query, ct);
    public Task<JsonElement> GetPrivateWorkerAsync(string workerId, CancellationToken ct = default) => Get("/v0/private-workers/" + Id(workerId), null, ct);
    public Task<JsonElement> ListWorkerPoolsAsync(object? query = null, CancellationToken ct = default) => Get("/v0/private-workers/pools", query, ct);
    public Task<JsonElement> CreateWorkerPoolAsync(object body, CancellationToken ct = default) => Post("/v0/private-workers/pools", body, ct);
    public Task<JsonElement> DeleteWorkerPoolAsync(object query, CancellationToken ct = default) => Send(HttpMethod.Delete, "/v0/private-workers/pools", null, query, ct);
    public Task<JsonElement> ListPendingRequestsAsync(object? query = null, CancellationToken ct = default) => Get("/v0/private-workers/pending-requests", query, ct);
    public IAsyncEnumerable<CursorSseEvent> StreamPendingRequestsAsync(object query, string? lastEventId = null, CancellationToken ct = default)
    {
        var element = JsonSerializer.SerializeToElement(query, Json);
        if (!element.TryGetProperty("cursor", out var cursor) || cursor.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cursor.GetString()))
            throw new ArgumentException("Cursor pending-request stream requires query.cursor; on cursor_expired re-list pending requests explicitly.");
        return Stream("/v0/private-workers/pending-requests/stream", query, lastEventId, ct);
    }
    public Task<JsonElement> ClaimPendingRequestAsync(object body, CancellationToken ct = default) => Post("/v0/private-workers/claim", body, ct);
    public Task<JsonElement> CreateWorkerTokensAsync(object body, CancellationToken ct = default) => Post("/v0/private-workers/tokens", body, ct);
    public Task<JsonElement> ReleaseWorkerClaimAsync(string claimId, CancellationToken ct = default) => Post("/v0/private-workers/claims/" + Id(claimId) + "/release", null, ct);

    private HttpRequestMessage Request(HttpMethod method, string path, object? query)
    {
        var request = new HttpRequestMessage(method, Host + path + Query(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return request;
    }

    private static string Query(object? query)
    {
        if (query is null) return "";
        var value = JsonSerializer.SerializeToElement(query, Json);
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Cursor query must be a JSON object.");
        var pairs = new List<string>();
        foreach (var p in value.EnumerateObject())
        {
            if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var values = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToArray() : [p.Value];
            foreach (var v in values)
            {
                var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
                pairs.Add(Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(text));
            }
        }
        return pairs.Count == 0 ? "" : "?" + string.Join("&", pairs);
    }

    private async Task<JsonElement> Send(HttpMethod method, string path, object? body, object? query, CancellationToken ct, bool retry = true)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = Request(method, path, query);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (retry && method == HttpMethod.Get && attempt < 3 && Retryable(response.StatusCode))
            {
                await Task.Delay(Delay(response, attempt), ct);
                continue;
            }
            return await Read(response, ct);
        }
    }

    private static bool Retryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status is 500 or 502 or 503 or 504;
    private static TimeSpan Delay(HttpResponseMessage response, int attempt)
    {
        var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
            ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 30000));
    }
    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement raw;
        try { raw = string.IsNullOrWhiteSpace(text) ? JsonSerializer.SerializeToElement(new { }) : JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { raw = JsonSerializer.SerializeToElement(new { body = text }); }
        if (!response.IsSuccessStatusCode) throw new CursorApiException(response.StatusCode, raw);
        return raw;
    }

    private async IAsyncEnumerable<CursorSseEvent> Stream(string path, object? query, string? lastEventId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, query);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(lastEventId)) request.Headers.Add("Last-Event-ID", lastEventId);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) { await Read(response, ct); yield break; }
        var retention = response.Headers.TryGetValues("X-Cursor-Stream-Retention-Seconds", out var headers) ? headers.FirstOrDefault() : null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string name = "message";
        string? id = null;
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    using var document = JsonDocument.Parse(data.ToString());
                    yield return new CursorSseEvent(name, id, document.RootElement.Clone(), retention);
                }
                if (line is null) yield break;
                name = "message"; id = null; data.Clear();
                continue;
            }
            if (line.StartsWith(':')) continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "event": name = value; break;
                case "id": if (!value.Contains('\0')) id = value; break;
                case "data": if (data.Length > 0) data.Append('\n'); data.Append(value); break;
            }
        }
    }
}

public sealed record CursorSseEvent(string Event, string? Id, JsonElement Data, string? RetentionSeconds = null);

/// <summary>Raw upstream errors are accessible without placing secret response payloads in exception messages.</summary>
public sealed class CursorApiException : HttpRequestException
{
    public JsonElement Raw { get; }
    public string? Code { get; }
    public CursorApiException(HttpStatusCode status, JsonElement raw)
        : base($"Cursor API failed (HTTP {(int)status}). Inspect Code and Raw for upstream details.", null, status)
    {
        Raw = raw.Clone();
        var error = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : raw;
        Code = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
    }
}
