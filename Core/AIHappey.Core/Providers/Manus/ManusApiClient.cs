using System.Net;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Manus;

/// <summary>Conversation-only transport. No endpoint dispatcher or management methods.</summary>
internal sealed class ManusApiClient(HttpClient http, HttpClient transfers, string key)
{
    private const string Host = "https://api.manus.ai/v2/";
    public Task<JsonElement> CreateTask(object body, CancellationToken ct) => Send("task.create", body, null, ct);
    public Task<JsonElement> SendMessage(object body, CancellationToken ct) => Send("task.sendMessage", body, null, ct);
    public Task<JsonElement> ConfirmAction(object body, CancellationToken ct) => Send("task.confirmAction", body, null, ct);
    public Task<JsonElement> TaskDetail(string task, CancellationToken ct) => Send("task.detail", null, new { task_id = task }, ct);
    public Task<JsonElement> Messages(object query, CancellationToken ct) => Send("task.listMessages", null, query, ct);
    public Task<JsonElement> Agents(CancellationToken ct) => Send("agent.list", null, new { }, ct);
    public Task<JsonElement> AgentDetail(string agent, CancellationToken ct) => Send("agent.detail", null, new { agent_id = agent }, ct);
    public Task<JsonElement> FileDetail(string file, CancellationToken ct) => Send("file.detail", null, new { file_id = file }, ct);

    public async Task<string> Upload(string filename, byte[] bytes, CancellationToken ct)
    {
        if (bytes.LongLength > 512L * 1024 * 1024) throw new ArgumentException("Manus uploads cannot exceed 512 MB.");
        var record = await Send("file.upload", new { filename }, null, ct);
        var url = record.GetProperty("upload_url").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Invalid Manus upload URL.");
        // Dedicated, credential-free client. Do not copy gateway or Manus headers.
        using var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(bytes) };
        using var response = await transfers.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var id = record.GetProperty("file").GetProperty("id").GetString()!;
        var detail = await FileDetail(id, ct);
        if (detail.GetProperty("file").GetProperty("status").GetString() != "uploaded")
            throw new InvalidOperationException("Manus file upload is not ready.");
        return id;
    }

    private async Task<JsonElement> Send(string endpoint, object? body, object? query, CancellationToken ct)
    {
        var suffix = query is null ? "" : "?" + string.Join("&", JsonSerializer.SerializeToElement(query, JsonSerializerOptions.Web)
            .EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).Select(p =>
                Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText())));
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, Host + endpoint + suffix);
            request.Headers.Add("x-manus-api-key", key);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (body is null && attempt < 3 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode is 500 or 502 or 503 or 504))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, 30000)), ct);
                continue;
            }
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonElement raw;
            try { using var doc = JsonDocument.Parse(text); raw = doc.RootElement.Clone(); }
            catch (JsonException) { throw new HttpRequestException($"Manus returned a non-JSON response (HTTP {(int)response.StatusCode}).", null, response.StatusCode); }
            if (!response.IsSuccessStatusCode || raw.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                throw new ManusApiException(response.StatusCode, raw);
            if (!raw.TryGetProperty("ok", out ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Manus response omitted its success envelope.");
            return raw;
        }
    }
}

public sealed class ManusApiException : HttpRequestException
{
    public JsonElement Raw { get; }
    public string? Code { get; }
    public string? RequestId { get; }
    internal ManusApiException(HttpStatusCode status, JsonElement raw)
        : base($"Manus API failed (HTTP {(int)status}); inspect Code, RequestId and Raw.", null, status)
    {
        Raw = raw.Clone();
        RequestId = raw.TryGetProperty("request_id", out var id) ? id.GetString() : null;
        Code = raw.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
