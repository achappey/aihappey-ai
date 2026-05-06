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

    private static void ApplyRequestHeaders(
        this HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return;

        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                continue;

            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value);
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

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
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        options.CleanUnsafeReasoningReplay();

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.ApplyRequestHeaders(headers);
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
        IReadOnlyDictionary<string, string>? headers = null,
        [EnumeratorCancellation] CancellationToken ct = default
)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        options.CleanUnsafeReasoningReplay();

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.ApplyRequestHeaders(headers);
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

        await foreach (var evt in ReadResponseSseEventsAsync(reader, captureSink, providerId, ct))
            yield return evt;
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

        await foreach (var evt in ReadResponseSseEventsAsync(reader, captureSink, providerId: null, ct))
            yield return evt;
    }


    private static async IAsyncEnumerable<ResponseStreamPart> ReadResponseSseEventsAsync(
        StreamReader reader,
        ProviderBackendCaptureSink? captureSink,
        string? providerId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dataBuilder = new StringBuilder();

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, ct);

            if (line.Length == 0)
            {
                if (!TryTakeSseDataEvent(dataBuilder, out var data))
                    continue;

                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                    yield break;

                yield return ParseResponseSseEvent(data, providerId);
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            AppendSseDataLine(dataBuilder, line);
        }

        if (TryTakeSseDataEvent(dataBuilder, out var trailingData)
            && !string.Equals(trailingData, "[DONE]", StringComparison.OrdinalIgnoreCase))
        {
            yield return ParseResponseSseEvent(trailingData, providerId);
        }
    }

    private static void AppendSseDataLine(StringBuilder builder, string line)
    {
        var data = line["data:".Length..];
        if (data.Length > 0 && data[0] == ' ')
            data = data[1..];

        if (builder.Length > 0)
            builder.Append('\n');

        builder.Append(data);
    }

    private static bool TryTakeSseDataEvent(StringBuilder builder, out string data)
    {
        if (builder.Length == 0)
        {
            data = string.Empty;
            return false;
        }

        data = builder.ToString().Trim();
        builder.Clear();
        return data.Length > 0;
    }

    private static ResponseStreamPart ParseResponseSseEvent(string data, string? providerId)
    {
        ResponseStreamPart? evt;
        try
        {
            evt = JsonSerializer.Deserialize<ResponseStreamPart>(data);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to parse SSE json event: {data}", ex);
        }

        if (evt is null)
            throw new InvalidOperationException($"Parsed SSE event was null: {data}");

        if (!string.IsNullOrEmpty(providerId))
        {
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
        }

        return evt;
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


    public static ResponseRequest CleanUnsafeReasoningReplay(this ResponseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Input is null || request.Input.IsText || request.Input.Items is null)
            return request;

        var cleaned = new List<ResponseInputItem>();

        foreach (var item in request.Input.Items)
        {
            if (item is not ResponseReasoningItem reasoning)
            {
                cleaned.Add(item);
                continue;
            }

            // Raw/summarized reasoning is observable output, not replayable provider state.
            if (string.IsNullOrWhiteSpace(reasoning.EncryptedContent))
                continue;

            // Keep only opaque provider state. Do not replay summaries/content back as context.
            cleaned.Add(new ResponseReasoningItem
            {
                Id = reasoning.Id,
                EncryptedContent = reasoning.EncryptedContent,
                Summary = []
            });
        }

        request.Input = new ResponseInput(cleaned);
        return request;
    }

}



