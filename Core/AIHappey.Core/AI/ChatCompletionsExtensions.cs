using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.AI;

public static class ChatCompletionsExtensions
{
    private static readonly MediaTypeWithQualityHeaderValue AcceptJson = new("application/json");
    private static readonly MediaTypeWithQualityHeaderValue AcceptSse = new("text/event-stream");

    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ChatCompletion> GetChatCompletion(
        this HttpClient client,
        ChatCompletionOptions options,
        string relativeUrl = "v1/chat/completions",
        CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        var payload = JsonSerializer.SerializeToElement(options);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await ThrowIfNotSuccess(resp, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<ChatCompletion>(stream, cancellationToken: ct).ConfigureAwait(false);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");

        return result;
    }

    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ChatCompletionUpdate> GetChatCompletionUpdates(
        this HttpClient client,
        ChatCompletionOptions options,
        string relativeUrl = "v1/chat/completions",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (client is null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = JsonSerializer.SerializeToElement(options);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await ThrowIfNotSuccess(resp, ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) yield break;

            if (line.Length == 0) continue; // keepalive

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;

            if (data == "[DONE]") yield break;

            ChatCompletionUpdate? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ChatCompletionUpdate>(data);
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

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }
}



