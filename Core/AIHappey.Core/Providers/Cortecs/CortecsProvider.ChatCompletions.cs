using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    public async Task<ChatCompletion> ChatCompletionsCompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var payload = BuildCortecsChatPayload(options, stream: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cortecs error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);

        var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = doc.RootElement.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = doc.RootElement.TryGetProperty("created", out var cEl) && cEl.TryGetInt64(out var epoch)
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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

    public async IAsyncEnumerable<ChatCompletionUpdate> ChatCompletionsCompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildCortecsChatPayload(options, stream: true);
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
            throw new HttpRequestException($"Cortecs stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
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
                JsonNode? root = JsonNode.Parse(data);
                var inner = root?["data"];
                evt = inner is null ? null : inner.Deserialize<ChatCompletionUpdate>(JsonSerializerOptions.Web);
            }

            if (evt is not null)
                yield return evt;
        }
    }

    private static object BuildCortecsChatPayload(ChatCompletionOptions options, bool stream)
    {
        var messages = options.Messages.ToCortecsMessages().ToList();
        var tools = options.Tools is { } t && t.Any()
            ? t.ToCortecsTools().ToList()
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["response_format"] = options.ResponseFormat,
            ["tools"] = tools,
            ["tool_choice"] = options.ToolChoice,
            ["parallel_tool_calls"] = options.ParallelToolCalls
        };

        // ChatCompletionOptions is intentionally minimal, but callers may provide extended
        // provider-specific fields that we can preserve by reading the serialized shape.
        var root = JsonSerializer.SerializeToElement(options, JsonSerializerOptions.Web);

        AddIfPresent(root, payload, "preference");
        AddIfPresent(root, payload, "allowed_providers");
        AddIfPresent(root, payload, "eu_native");
        AddIfPresent(root, payload, "allow_quantization");
        AddIfPresent(root, payload, "max_tokens");
        AddIfPresent(root, payload, "top_p");
        AddIfPresent(root, payload, "frequency_penalty");
        AddIfPresent(root, payload, "presence_penalty");
        AddIfPresent(root, payload, "stop");
        AddIfPresent(root, payload, "logprobs");
        AddIfPresent(root, payload, "seed");
        AddIfPresent(root, payload, "n");
        AddIfPresent(root, payload, "prediction");
        AddIfPresent(root, payload, "safe_prompt");

        return payload;
    }

    private static void AddIfPresent(JsonElement root, IDictionary<string, object?> payload, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return;

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        payload[propertyName] = JsonSerializer.Deserialize<object>(prop.GetRawText(), JsonSerializerOptions.Web);
    }
}

