using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamChatAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var payload = BuildInworldChatPayload(chatRequest, stream: true);
        var json = JsonSerializer.Serialize(payload, InworldJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "llm/v1alpha/completions:completeChat")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Inworld stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? streamId = null;
        bool textStarted = false;

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;
        int? reasoningTokens = null;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) continue;

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                var result = GetResultElement(root);

                streamId ??= result.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                streamId ??= Guid.NewGuid().ToString("n");

                if (result.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("promptTokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                        promptTokens = pt.GetInt32();
                    if (usageEl.TryGetProperty("completionTokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                        completionTokens = ct.GetInt32();
                    if (usageEl.TryGetProperty("reasoningTokens", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        reasoningTokens = rt.GetInt32();

                    totalTokens = promptTokens + completionTokens;
                }

                if (result.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choicesEl.EnumerateArray())
                    {
                        if (choice.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                        {
                            if (msgEl.TryGetProperty("content", out var contentEl))
                            {
                                var content = contentEl.ValueKind == JsonValueKind.String
                                    ? contentEl.GetString()
                                    : contentEl.GetRawText();

                                if (!string.IsNullOrWhiteSpace(content))
                                {
                                    if (!textStarted)
                                    {
                                        yield return streamId.ToTextStartUIMessageStreamPart();
                                        textStarted = true;
                                    }

                                    yield return new TextDeltaUIMessageStreamPart
                                    {
                                        Id = streamId,
                                        Delta = content
                                    };
                                }
                            }
                        }

                        if (choice.TryGetProperty("finishReason", out var frEl)
                            && frEl.ValueKind == JsonValueKind.String)
                        {
                            var finishReason = MapFinishReason(frEl.GetString());

                            if (textStarted)
                            {
                                yield return streamId.ToTextEndUIMessageStreamPart();
                                textStarted = false;
                            }

                            yield return finishReason.ToFinishUIPart(
                                model: chatRequest.Model,
                                outputTokens: completionTokens,
                                inputTokens: promptTokens,
                                totalTokens: totalTokens,
                                temperature: chatRequest.Temperature,
                                reasoningTokens: reasoningTokens);
                            yield break;
                        }
                    }
                }
            }
        }

        if (textStarted && streamId is not null)
            yield return streamId.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature,
            reasoningTokens: reasoningTokens);
    }
}
