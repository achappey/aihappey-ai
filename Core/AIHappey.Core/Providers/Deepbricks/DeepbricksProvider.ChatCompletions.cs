using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Deepbricks;

public partial class DeepbricksProvider
{
    private async Task<ChatCompletion> CompleteChatCoreAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildDeepbricksChatPayload(options, stream: false);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Deepbricks error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

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

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingCoreAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildDeepbricksChatPayload(options, stream: true);
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
            throw new HttpRequestException($"Deepbricks stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.Length == 0 || line.StartsWith(':'))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data is "[DONE]" or "[done]")
                yield break;

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

    private static object BuildDeepbricksChatPayload(ChatCompletionOptions options, bool stream)
    {
        var messages = options.Messages.ToDeepbricksMessages().ToList();

        var tools = options.Tools is { } t && t.Any()
            ? t.ToDeepbricksTools().ToList()
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
            ["stream"] = stream,
            ["stream_options"] = stream ? new { include_usage = true } : null
        };
    }
}

