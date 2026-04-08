using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{

    private async Task<ChatCompletion> CompleteChatInternalAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildParallelChatPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Parallel chat completion error ({(int)resp.StatusCode}): {err}");
        }

        var result = await resp.Content.ReadFromJsonAsync<ChatCompletion>(cancellationToken)
                     ?? throw new InvalidOperationException("Parallel returned an empty chat completion response.");

        return result;
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var raw in StreamChatRawChunksAsync(options, cancellationToken))
        {
            var update = JsonSerializer.Deserialize<ChatCompletionUpdate>(raw, Json);
            if (update is null)
                continue;

            if (string.IsNullOrWhiteSpace(update.Object))
                update.Object = "chat.completion.chunk";

            yield return update;
        }
    }

    private async IAsyncEnumerable<string> StreamChatRawChunksAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildParallelChatPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Parallel chat stream error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? eventType = null;
        var dataLines = new List<string>();

        static string? FlushData(List<string> lines)
        {
            if (lines.Count == 0)
                return null;

            var merged = string.Join("\n", lines);
            lines.Clear();

            if (string.IsNullOrWhiteSpace(merged))
                return null;

            return merged.Trim();
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                var payloadData = FlushData(dataLines);
                if (payloadData is null)
                {
                    eventType = null;
                    continue;
                }

                if (payloadData is "[DONE]" or "[done]")
                    yield break;

                yield return payloadData;
                eventType = null;
                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = line["event:".Length..].Trim();
                _ = eventType;
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line["data:".Length..].TrimStart());
            }
        }

        var trailing = FlushData(dataLines);
        if (!string.IsNullOrWhiteSpace(trailing) && trailing is not "[DONE]" and not "[done]")
            yield return trailing;
    }

    private static Dictionary<string, object?> BuildParallelChatPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = NormalizeCompletionMessages(options.Messages),
            ["stream"] = stream,
            ["response_format"] = options.ResponseFormat,
            ["temperature"] = options.Temperature,
            ["parallel_tool_calls"] = options.ParallelToolCalls,
        };

        if (options.Tools?.Any() == true)
            payload["tools"] = options.Tools;

        if (!string.IsNullOrWhiteSpace(options.ToolChoice))
            payload["tool_choice"] = options.ToolChoice;

        return payload;
    }

}

