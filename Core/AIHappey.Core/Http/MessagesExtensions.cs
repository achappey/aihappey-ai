using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Messages;
using System.Net.Http.Json;

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
        Dictionary<string, string>? headers = null,
        string relativeUrl = "v1/messages",
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

        return await resp.Content.ReadFromJsonAsync<MessagesResponse>(ct) ?? throw new Exception("Something went wrong");
    }

    public static async IAsyncEnumerable<MessageStreamPart> PostMessagesStreaming(
        this HttpClient client,
        MessagesRequest payload,
        Dictionary<string, string>? headers = null,
        string relativeUrl = "v1/messages",
        [EnumeratorCancellation] CancellationToken ct = default)
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

        string? line;
        while (!ct.IsCancellationRequested &&
               (line = await reader.ReadLineAsync(ct)) != null)
        {
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