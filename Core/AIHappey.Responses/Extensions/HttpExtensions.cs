using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Abstractions.Http;
using AIHappey.Responses.Streaming;

namespace AIHappey.Responses.Extensions;

public static class HttpExtensions
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeWithQualityHeaderValue AcceptSse = new("text/event-stream");

    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ResponseResult> GetResponses(
        this HttpClient client,
        ResponseRequest options,
        string? providerId,
        string relativeUrl = "v1/responses",
        JsonElement? extraRootProperties = null,
        ProviderBackendCaptureRequest? capture = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        var payload = BuildPayload(options, extraRootProperties);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("responses", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<ResponseResult>(body, ResponseJson.Default);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");

        if (!string.IsNullOrEmpty(providerId))
            result.Model = $"{providerId}/{result.Model}";

        return result;
    }

    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ResponseStreamPart> GetResponsesUpdates(
        this HttpClient client,
        ResponseRequest options,
        string providerId,
        string relativeUrl = "v1/responses",
        JsonElement? extraRootProperties = null,
        ProviderBackendCaptureRequest? capture = null,
        [EnumeratorCancellation] CancellationToken ct = default
)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = BuildPayload(options, extraRootProperties);

        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("responses", resp, capture);

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, ct);

            if (line is null) yield break;

            if (line.Length == 0) continue; // keepalive

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;

            if (data == "[DONE]") yield break;

            ResponseStreamPart? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ResponseStreamPart>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse SSE json event: {data}", ex);
            }

            switch (evt)
            {
                case ResponseCompleted responseCompleted:
                    responseCompleted.Response.Model = $"{providerId}/{responseCompleted.Response.Model}";
                    break;

                case ResponseCreated responseCreated:
                    responseCreated.Response.Model = $"{providerId}/{responseCreated.Response.Model}";
                    break;

                case ResponseInProgress responseInProgress:
                    responseInProgress.Response.Model = $"{providerId}/{responseInProgress.Response.Model}";
                    break;
            }


            if (evt is not null)
                yield return evt;
        }
    }



    [Obsolete]
    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ResponseResult> GetResponses(
            this HttpClient client,
            ResponseRequest options,
            string relativeUrl = "v1/responses",
            JsonElement? extraRootProperties = null,
            ProviderBackendCaptureRequest? capture = null,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        var payload = BuildPayload(options, extraRootProperties);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("responses", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<ResponseResult>(body, ResponseJson.Default);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");


        return result;
    }

    [Obsolete]
    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ResponseStreamPart> GetResponsesUpdates(
            this HttpClient client,
            ResponseRequest options,
            string relativeUrl = "v1/responses",
            JsonElement? extraRootProperties = null,
            ProviderBackendCaptureRequest? capture = null,
            [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = BuildPayload(options, extraRootProperties);

        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("responses", resp, capture);

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, ct);

            if (line is null) yield break;

            if (line.Length == 0) continue; // keepalive

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;

            if (data == "[DONE]") yield break;

            ResponseStreamPart? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ResponseStreamPart>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse SSE json event: {data}", ex);
            }


            if (evt is not null)
                yield return evt;
        }
    }




    private static async Task ThrowIfNotSuccess(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }

    private static JsonElement BuildPayload(ResponseRequest options, JsonElement? extraRootProperties)
    {
        var payload = JsonSerializer.SerializeToElement(options, ResponseJson.Default);

        if (extraRootProperties is not JsonElement extra)
            return payload;

        if (extra.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return payload;

        if (extra.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Extra responses root properties must be a JSON object.");

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in payload.EnumerateObject())
            merged[property.Name] = property.Value.Clone();

        foreach (var property in extra.EnumerateObject())
        {
            merged[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(merged, ResponseJson.Default);
    }
}



