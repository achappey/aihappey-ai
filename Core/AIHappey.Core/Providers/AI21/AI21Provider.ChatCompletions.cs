using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.AI21;

public sealed partial class AI21Provider
{
    async Task<ChatCompletion> IModelProvider.CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        if (options is null) throw new ArgumentNullException(nameof(options));

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        if (options.Tools?.Any() == true)
        {
            // AI21: tools require non-stream (OK), but their schema is not identical to OpenAI.
            // We keep the raw tool definitions and pass them through.
        }

        var payload = BuildAi21ChatPayload(options, stream: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"AI21 error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        // AI21 response is close to OpenAI, but we keep Choices/Usage as opaque objects.
        using var doc = JsonDocument.Parse(raw);

        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = doc.RootElement.TryGetProperty("created", out var cEl) && cEl.TryGetInt64(out var epoch) ? epoch : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        object? usage = null;
        if (doc.RootElement.TryGetProperty("usage", out var uEl))
            usage = JsonSerializer.Deserialize<object>(uEl.GetRawText(), JsonSerializerOptions.Web);

        object[] choices = [];
        if (doc.RootElement.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
            choices = JsonSerializer.Deserialize<object[]>(chEl.GetRawText(), JsonSerializerOptions.Web) ?? [];

        return new ChatCompletion
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Created = created,
            Model = model ?? options.Model,
            Choices = choices,
            Usage = usage
        };
    }

    async IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        if (options is null) throw new ArgumentNullException(nameof(options));

        if (options.Tools?.Any() == true)
            throw new NotSupportedException("AI21 does not support tools with stream=true.");

        var payload = BuildAi21ChatPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"AI21 stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.Length == 0) continue;
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data is "[DONE]" or "[done]") yield break;

            ChatCompletionUpdate? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, JsonSerializerOptions.Web);
            }
            catch
            {
                // If AI21 ever wraps: {"data":{...}} or similar, fall back to extracting a data node.
                JsonNode? root = JsonNode.Parse(data);
                var inner = root?["data"];
                evt = inner is null ? null : inner.Deserialize<ChatCompletionUpdate>(JsonSerializerOptions.Web);
            }

            if (evt is not null)
                yield return evt;
        }
    }

    private static object BuildAi21ChatPayload(ChatCompletionOptions options, bool stream)
    {
        // AI21 requires messages[].content to be a string, and tool_calls.function.arguments to be a JSON string.
        var messages = options.Messages.ToAi21Messages().ToList();

        var tools = options.Tools is { } t && t.Any()
            ? t.ToAi21Tools().ToList()
            : null;

        return new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["temperature"] = options.Temperature,
            ["top_p"] = null,
            ["max_tokens"] = null,
            ["response_format"] = options.ResponseFormat,
            ["tools"] = tools,
            ["tool_choice"] = options.ToolChoice,
            ["stream"] = stream
        };
    }
}

