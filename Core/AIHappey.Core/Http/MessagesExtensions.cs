using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Abstractions.Http;
using AIHappey.Messages;

namespace AIHappey.Core.AI;

public static class MessagesExtensions
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeWithQualityHeaderValue AcceptSse = new("text/event-stream");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<MessagesResponse> PostMessages(
        this HttpClient client,
        MessagesRequest payload,
        string providerId,
        Dictionary<string, string>? headers = null,
        string relativeUrl = "v1/messages",
        ProviderBackendCaptureRequest? capture = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);

        if (headers != null)
        {
            foreach (var h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json),
            Encoding.UTF8,
            "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("messages", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<MessagesResponse>(body, Json)
            ?? throw new Exception("Something went wrong");

        result.Model = $"{providerId}/{result.Model}";

        return result;
    }

    public static async IAsyncEnumerable<MessageStreamPart> PostMessagesStreaming(
        this HttpClient client,
        MessagesRequest payload,
        string providerId,
        Dictionary<string, string>? headers = null,
        string relativeUrl = "v1/messages",
        [EnumeratorCancellation] CancellationToken ct = default,
        ProviderBackendCaptureRequest? capture = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (headers != null)
        {
            foreach (var h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json),
            Encoding.UTF8,
            "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("messages", resp, capture);

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, ct);

            if (line.Length == 0) continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();

            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;

            MessageStreamPart? evt;
            try
            {
                evt = JsonSerializer.Deserialize<MessageStreamPart>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse SSE json event: {data}", ex);
            }

            if (evt?.Type == "message.start" &&
                evt?.AdditionalProperties is { } props &&
                props.TryGetValue("model", out var modelObj) &&
                modelObj is JsonElement je &&
                je.ValueKind == JsonValueKind.String)
            {
                var model = je.GetString();

                if (!string.IsNullOrEmpty(model))
                {
                    var newModel = $"{providerId}/{model}";

                    props["model"] = JsonDocument.Parse($"\"{newModel}\"").RootElement.Clone();
                }
            }

            if (evt != null)
                yield return evt;
        }
    }

    [Obsolete]
    public static async Task<MessagesResponse> PostMessages(
            this HttpClient client,
            MessagesRequest payload,
            Dictionary<string, string>? headers = null,
            string relativeUrl = "v1/messages",
            ProviderBackendCaptureRequest? capture = null,
            CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);

        if (headers != null)
        {
            foreach (var h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json),
            Encoding.UTF8,
            "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("messages", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<MessagesResponse>(body, Json)
            ?? throw new Exception("Something went wrong");


        return result;
    }

    [Obsolete]
    public static async IAsyncEnumerable<MessageStreamPart> PostMessagesStreaming(
            this HttpClient client,
            MessagesRequest payload,
            Dictionary<string, string>? headers = null,
            string relativeUrl = "v1/messages",
            [EnumeratorCancellation] CancellationToken ct = default,
            ProviderBackendCaptureRequest? capture = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (headers != null)
        {
            foreach (var h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, Json),
            Encoding.UTF8,
            "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("messages", resp, capture);

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
            if (captureSink is not null)
                await captureSink.WriteLineAsync(line, ct);

            if (line.Length == 0) continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();

            if (data.Length == 0) continue;
            if (data == "[DONE]") yield break;

            MessageStreamPart? evt;
            try
            {
                evt = JsonSerializer.Deserialize<MessageStreamPart>(data);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to parse SSE json event: {data}", ex);
            }


            if (evt != null)
                yield return evt;
        }
    }


    private static async Task ThrowIfNotSuccess(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }
}
