using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
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
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);
        payload.Tools = [.. payload.Tools?.DistinctBy(a => a.Name) ?? []];

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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        payload.Tools = [.. payload.Tools?.DistinctBy(a => a.Name) ?? []];
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


            if (evt?.Type == "message_start")
            {
                var model = evt.Message?.Model;

                if (!string.IsNullOrEmpty(model))
                {
                    var newModel = $"{providerId}/{model}";

                    evt.Message?.Model = newModel;
                }
            }

            if (evt != null)
                yield return evt;
        }
    }
    
    private static async Task ThrowIfNotSuccess(
        HttpResponseMessage resp,
        CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return;

        var body = resp.Content is null
            ? null
            : await resp.Content.ReadAsStringAsync(ct);

        var message = TryGetErrorMessage(body);

        throw new HttpRequestException(
            message ?? $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    private static string? TryGetErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Anthropic / OpenAI style:
            // { "error": { "message": "..." } }
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
            }

            // Generic:
            // { "message": "..." }
            if (root.TryGetProperty("message", out var rootMessage) &&
                rootMessage.ValueKind == JsonValueKind.String)
            {
                return rootMessage.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON response.
        }

        return body;
    }
}
