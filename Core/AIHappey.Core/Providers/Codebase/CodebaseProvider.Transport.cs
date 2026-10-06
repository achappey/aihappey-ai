using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Core.Diagnostics;

namespace AIHappey.Core.Providers.Codebase;

public partial class CodebaseProvider
{
    private sealed record Reply(JsonElement Raw, Dictionary<string, string[]> Headers);

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, Dictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path);
        // Never forward gateway authentication, host, or arbitrary incoming HTTP headers.
        foreach (var pair in headers ?? [])
            if (!pair.Key.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                && !pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                request.Headers.Add(pair.Key, pair.Value);
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) { request.Dispose(); throw new InvalidOperationException("No Codebase API key."); }
        request.Headers.Add("X-API-Key", key);
        return request;
    }

    private async Task<Reply> SendJsonAsync(HttpMethod method, string path, object? body,
        Dictionary<string, string>? headers, string? idempotencyKey, CancellationToken ct, bool capture = true)
    {
        using var request = CreateRequest(method, path, headers);
        if (idempotencyKey is not null)
        {
            request.Headers.Remove("Idempotency-Key");
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        var operationId = _debug.Enabled && capture ? Guid.NewGuid().ToString("N") : "";
        if (_debug.Enabled && capture && request.Content is not null)
            await _debug.EmitAsync(GetIdentifier(), path, operationId, "request-body",
                ProviderDebugPayload.FromText(await request.Content.ReadAsStringAsync(ct), "application/json"), ct);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (_debug.Enabled && capture)
            await _debug.EmitAsync(GetIdentifier(), path, operationId, "response-body",
                ProviderDebugPayload.FromText(raw, response.Content.Headers.ContentType?.MediaType ?? "application/json"), ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Codebase API ({(int)response.StatusCode}) at {path}: {raw}", null, response.StatusCode);
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new(string.IsNullOrWhiteSpace(raw) ? Element(new { }) : JsonDocument.Parse(raw).RootElement.Clone(), responseHeaders);
    }

    private static string SessionPath(string id) => "api/v1/builds/" + Uri.EscapeDataString(id);

    private async Task TraceEventsAsync(string sessionId, CancellationTokenSource lifetime)
    {
        var path = SessionPath(sessionId) + "/events";
        var ct = lifetime.Token;
        try
        {
            using var request = CreateRequest(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/event-stream");
            var operationId = Guid.NewGuid().ToString("N");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(ct);
                await _debug.EmitAsync(GetIdentifier(), path, operationId, "response-body",
                    ProviderDebugPayload.FromText(raw, response.Content.Headers.ContentType?.MediaType ?? "application/json"), ct);
                response.EnsureSuccessStatusCode();
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var observed = new DebugResponseStream(stream, _debug, GetIdentifier(), path, operationId,
                response.Content.Headers.ContentType?.MediaType ?? "text/event-stream");
            var buffer = new byte[4096];
            // No event history, semantic parsing, reconnect, or response-wide buffering.
            while (await observed.ReadAsync(buffer, ct) != 0) { }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch
        {
            // Propagate diagnostic transport/sink failures rather than dropping frames.
            await lifetime.CancelAsync();
            throw;
        }
    }

    private async Task<Reply> StatusAsync(string sessionId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await SendJsonAsync(HttpMethod.Get, SessionPath(sessionId) + "/status", null, null, null, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct); }
        }
    }
}
