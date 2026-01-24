using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Hyperstack;

public partial class HyperstackProvider
{
    private static readonly JsonSerializerOptions completionOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var messages = chatRequest.Messages.ToCompletionMessages();
        var payload = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["max_tokens"] = chatRequest.MaxOutputTokens,
            ["temperature"] = chatRequest.Temperature,
            ["messages"] = messages
        };

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;

        string? id = null;
        bool started = false;
        StringBuilder fullMessageText = new();

        await foreach (var data in HyperstackStreamAsync(payload, cancellationToken))
        {
            JsonDocument doc = JsonDocument.Parse(data);

            var root = doc.RootElement;

            // ---------- Parse token usage if present ----------
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();

                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    completionTokens = ct.GetInt32();

                if (usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    totalTokens = tt.GetInt32();
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                        continue;

                    // ---- normal text delta ----
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            id ??= root.TryGetProperty("id", out var idEl) ? idEl.GetString() : Guid.NewGuid().ToString();

                            if (!started)
                            {
                                yield return id!.ToTextStartUIMessageStreamPart();
                                started = true;
                            }

                            fullMessageText.Append(text);

                            yield return new TextDeltaUIMessageStreamPart { Id = id!, Delta = text };
                        }
                    }

                }
            }

            doc.Dispose();
        }

        if (id is not null)
            yield return id.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature
        );
    }

    private async IAsyncEnumerable<string> HyperstackStreamAsync(Dictionary<string, object?> payload,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        payload["stream"] = true;

        var json = JsonSerializer.Serialize(payload, completionOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // ---------- 2) Send request as streaming SSE ----------
        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"API error: {err}";
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // buffers/state used across chunks
        var toolBuffers = new Dictionary<string, (string? ToolName, StringBuilder Args, int Index)>();
        var indexToKey = new Dictionary<int, string>();   // map streaming "index" -> canonical key (id or synthetic)
        var toolStartSent = new HashSet<string>();
        StringBuilder fullMessageText = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break; // EOF

            if (line.Length == 0 || line.StartsWith(":")) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..].Trim();
            if (data is "[DONE]" or "[done]") break;

            yield return data;
        }
    }

    private async Task<string> HyperstackCompletionAsync(Dictionary<string, object?> payload,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        payload["stream"] = false;

        var json = JsonSerializer.Serialize(payload, completionOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        // ---------- 2) Send request as streaming SSE ----------
        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"API error: {err}");
        }

        var stream = await resp.Content.ReadAsStringAsync(cancellationToken);

        return stream;
    }
}