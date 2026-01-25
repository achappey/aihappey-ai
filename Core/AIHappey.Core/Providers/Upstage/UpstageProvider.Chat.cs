using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using System.Net.Http.Headers;

namespace AIHappey.Core.Providers.Upstage;

public partial class UpstageProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
          ChatRequest chatRequest,
          [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var messages = chatRequest.Messages.ToUpstageMessages();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["stream"] = true,
            ["messages"] = messages,
            ["temperature"] = chatRequest.Temperature
        };

        if (chatRequest.TopP is not null)
            payload["top_p"] = chatRequest.TopP;

        if (chatRequest.MaxOutputTokens is not null)
            payload["max_tokens"] = chatRequest.MaxOutputTokens;

        if (chatRequest.ResponseFormat is not null)
            payload["response_format"] = chatRequest.ResponseFormat;

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Upstage stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? id = null;
        bool textStarted = false;

        int promptTokens = 0, completionTokens = 0, totalTokens = 0;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data is "[DONE]" or "[done]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            id ??= root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            id ??= Guid.NewGuid().ToString("n");

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
                    // delta content
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        {
                            var token = content.GetString();
                            if (!string.IsNullOrEmpty(token))
                            {
                                if (!textStarted)
                                {
                                    yield return id.ToTextStartUIMessageStreamPart();
                                    textStarted = true;
                                }

                                yield return new TextDeltaUIMessageStreamPart { Id = id, Delta = token };
                            }
                        }
                    }

                    // finish
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        var finish = fr.GetString() ?? "stop";
                        if (textStarted)
                        {
                            yield return id.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }

                        yield return finish.ToFinishUIPart(
                            model: chatRequest.Model,
                            outputTokens: completionTokens,
                            inputTokens: promptTokens,
                            totalTokens: totalTokens,
                            temperature: chatRequest.Temperature);
                        yield break;
                    }
                }
            }
        }

        if (textStarted)
            yield return id!.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }
}
