using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Abstractions.Http;
using AIHappey.ChatCompletions.Models;
using System.Text.Json.Serialization;

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
        JsonElement payload,
        string providerId,
        string relativeUrl = "v1/chat/completions",
        ProviderBackendCaptureRequest? capture = null,
        IReadOnlyDictionary<string, string>? headers = null,
                CancellationToken ct = default
)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.ApplyRequestHeaders(headers);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);

        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("chat-completions", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<ChatCompletion>(body, JsonSerializerOptions.Web);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");

        result.Model = $"{providerId}/{result.Model}";

        return result;
    }

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
    public static async Task<ChatCompletion> GetChatCompletion(
        this HttpClient client,
        ChatCompletionOptions options,
        string providerId,
        string relativeUrl = "v1/chat/completions",
        JsonElement? extraRootProperties = null,
        ProviderBackendCaptureRequest? capture = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        => await client.GetChatCompletion(BuildPayload(options, providerId, extraRootProperties), providerId, relativeUrl, capture, headers, ct);

    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ChatCompletionUpdate> GetChatCompletionUpdates(
        this HttpClient client,
        ChatCompletionOptions options,
        string providerId,
        string relativeUrl = "v1/chat/completions",
        JsonElement? extraRootProperties = null,
        ProviderBackendCaptureRequest? capture = null,
        IReadOnlyDictionary<string, string>? headers = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        options.StreamOptions ??= new StreamOptions();
        options.StreamOptions.IncludeUsage = true;

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.ApplyRequestHeaders(headers);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = BuildPayload(options, providerId, extraRootProperties);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("chat-completions", resp, capture);

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

            ChatCompletionUpdate? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ChatCompletionUpdate>(
                    NormalizeChatCompletionChunkJson(data));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse SSE json event: {data}", ex);
            }

            if (!string.IsNullOrEmpty(evt?.Model))
                evt.Model = $"{providerId}/{evt.Model}";

            if (evt?.Created == 0)
                evt?.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (evt is not null)
                yield return evt;
        }
    }

    private static string NormalizeChatCompletionChunkJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return json;

        if (!root.TryGetProperty("created", out var createdEl) ||
            createdEl.ValueKind != JsonValueKind.Number)
        {
            return json;
        }

        // Already valid long, no rewrite needed.
        if (createdEl.TryGetInt64(out _))
            return json;

        var createdDecimal = createdEl.GetDecimal();

        if (createdDecimal != decimal.Truncate(createdDecimal))
            return json;

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("created"))
                {
                    writer.WriteNumber("created", checked((long)createdDecimal));
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Obsolete]
    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ChatCompletion> GetChatCompletion(
            this HttpClient client,
            JsonElement payload,
            string relativeUrl = "v1/chat/completions",
            CancellationToken ct = default,
            ProviderBackendCaptureRequest? capture = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptJson);

        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        await ProviderBackendCapture.CaptureJsonAsync("chat-completions", resp, body, capture, ct);

        var result = JsonSerializer.Deserialize<ChatCompletion>(body, JsonSerializerOptions.Web);

        if (result is null)
            throw new InvalidOperationException($"Empty JSON response for {relativeUrl}.");

        return result;
    }

    [Obsolete]
    /// <summary>
    /// POST JSON and deserialize JSON response into T (non-stream).
    /// </summary>
    public static async Task<ChatCompletion> GetChatCompletion(
            this HttpClient client,
            ChatCompletionOptions options,
            string relativeUrl = "v1/chat/completions",
            CancellationToken ct = default,
            JsonElement? extraRootProperties = null,
            ProviderBackendCaptureRequest? capture = null)
            => await client.GetChatCompletion(BuildPayload(options, null, extraRootProperties), relativeUrl, ct, capture);

    [Obsolete]
    /// <summary>
    /// POST JSON with stream=true and parse SSE "data: {json}" events into TEvent.
    /// Ends on "data: [DONE]".
    /// </summary>
    public static async IAsyncEnumerable<ChatCompletionUpdate> GetChatCompletionUpdates(
            this HttpClient client,
            ChatCompletionOptions options,
            string relativeUrl = "v1/chat/completions",
            JsonElement? extraRootProperties = null,
            [EnumeratorCancellation] CancellationToken ct = default,
            ProviderBackendCaptureRequest? capture = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(relativeUrl)) throw new ArgumentNullException(nameof(relativeUrl));

        options.StreamOptions ??= new StreamOptions();
        options.StreamOptions.IncludeUsage = true;

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(AcceptSse);
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var payload = BuildPayload(options, null, extraRootProperties);
        req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await ThrowIfNotSuccess(resp, ct);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await using var captureSink = ProviderBackendCapture.BeginStreamCapture("chat-completions", resp, capture);

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

        var body = resp.Content is null ? "" : await resp.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
    }

    private static JsonElement BuildPayload(
        ChatCompletionOptions options,
        string? providerId,
        JsonElement? extraRootProperties,
        bool? stream = null)
    {
        var payload = JsonSerializer.SerializeToElement(options, Json);

        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Chat-completions payload must be a JSON object.");

        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // Always normalize through a dictionary.
        // This collapses duplicate keys caused by JsonExtensionData.
        foreach (var property in payload.EnumerateObject())
            merged[property.Name] = property.Value.Clone();

        // Internal-only request metadata should not leak to providers.
        merged.Remove("providerMetadata");

        // If your ChatCompletionOptions.Metadata is internal-only, keep this.
        // If you also support native OpenAI metadata, remove only the internal one before provider patch.
        merged.Remove("metadata");

        ApplyRootProperties(
            merged,
            extraRootProperties,
            overwriteExisting: false,
            sourceName: "Extra chat-completions root properties");

        var providerOptions = GetProviderOptions(options, providerId);

        ApplyRootProperties(
            merged,
            providerOptions,
            overwriteExisting: true,
            sourceName: $"Provider metadata for '{providerId}'",
            excluded:
            [
                "headers",
            "tools",
            "providerMetadata"
            ]);

        // Method invariant wins last.
        // Streaming reader must get SSE, non-stream reader must get JSON.
        if (stream is not null)
            merged["stream"] = JsonSerializer.SerializeToElement(stream.Value, Json);

        return JsonSerializer.SerializeToElement(merged, Json);
    }

    private static JsonElement? GetProviderOptions(
        ChatCompletionOptions options,
        string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return null;

        if (options.Metadata is null)
            return null;

        if (!options.Metadata.TryGetValue(providerId, out var obj))
            return null;

        return obj switch
        {
            null => null,
            JsonElement json => json,
            _ => JsonSerializer.SerializeToElement(obj, Json)
        };
    }

    private static void ApplyRootProperties(
        IDictionary<string, JsonElement> target,
        JsonElement? rootProperties,
        bool overwriteExisting,
        string sourceName,
        HashSet<string>? excluded = null)
    {
        if (rootProperties is not JsonElement root)
            return;

        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"{sourceName} must be a JSON object.");

        foreach (var property in root.EnumerateObject())
        {
            if (excluded?.Contains(property.Name) == true)
                continue;

            if (!overwriteExisting && target.ContainsKey(property.Name))
                continue;

            target[property.Name] = property.Value.Clone();
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}



