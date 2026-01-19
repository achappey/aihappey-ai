using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Net.Mime;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var url = "v2/chat";

        ApplyAuthHeader();
        var metadata = chatRequest.GetProviderMetadata<CohereProviderMetadata>(GetIdentifier());

        // ---- 1) Request body (simpel, geen tools/docs) ----
        var payload = new
        {
            model = chatRequest.Model,
            stream = true,
            messages = chatRequest.Messages.ToMessages(),
            max_tokens = chatRequest.MaxOutputTokens,
            temperature = chatRequest.Temperature,
            p = chatRequest.TopP,
            thinking = metadata?.Thinking,
            citation_options = metadata?.CitationOptions,
            priority = metadata?.Priority
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Text.EventStream));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var errText = await resp.Content.ReadAsStringAsync(cancellationToken);

            yield return $"Cohere stream error: {(string.IsNullOrWhiteSpace(errText) ? resp.ReasonPhrase : errText)}"
                .ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? currentStreamId = null;
        bool textStarted = false;
        bool reasoningStarted = false;
        bool sawMessageEnd = false;

        string modelId = chatRequest.Model;

        int inputTokens = 0, outputTokens = 0, totalTokens = 0;
        int? reasoningTokens = null;

        // Some servers use SSE header "event:", others put "event" in data JSON.
        string? sseEventHeader = null;

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) { sseEventHeader = null; continue; } // event separator
            if (line.StartsWith(':')) continue;                         // comment

            if (line.StartsWith("event: "))
            {
                sseEventHeader = line["event: ".Length..].Trim();
                continue;
            }
            if (!line.StartsWith("data: ")) continue;

            var dataStr = line["data: ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(dataStr)) continue;
            if (dataStr is "[DONE]" or "[done]") break;

            JsonNode? raw = JsonNode.Parse(dataStr);

            // Handle SDK-style wrapper: { "type":"json", "value": { ... } }
            if (raw?["type"]?.GetValue<string>() == "json" && raw?["value"] is JsonNode val)
                raw = val;

            // Determine event + data node
            var eventName = sseEventHeader ?? raw?["event"]?.GetValue<string>();
            var dataNode = raw?["data"] ?? raw; // some servers put payload directly

            if (string.IsNullOrWhiteSpace(eventName) || dataNode is null)
                continue;

            // Pick up model id if present anywhere
            modelId = raw?["model"]?.GetValue<string>() ?? modelId;

            switch (eventName)
            {
                case "message-start":
                    {
                        // Try to set a stream id if Cohere provides one
                        currentStreamId ??= dataNode?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n");
                        break;
                    }

                case "content-start":
                    {
                        // Prepare to send text start on first delta
                        currentStreamId ??= dataNode?["id"]?.GetValue<string>() ?? currentStreamId ?? Guid.NewGuid().ToString("n");
                        break;
                    }

                case "content-delta":
                    {
                        // path: data.delta.message.content.text
                        var delta = dataNode?["delta"]?["message"]?["content"]?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            currentStreamId ??= Guid.NewGuid().ToString("n");
                            if (!textStarted)
                            {
                                yield return currentStreamId.ToTextStartUIMessageStreamPart();
                                textStarted = true;
                            }

                            yield return new TextDeltaUIMessageStreamPart
                            {
                                Id = currentStreamId,
                                Delta = delta
                            };
                        }

                        var deltaThinking = dataNode?["delta"]?["message"]?["content"]?["thinking"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(deltaThinking))
                        {
                            currentStreamId ??= Guid.NewGuid().ToString("n");
                            if (!reasoningStarted)
                            {
                                yield return new ReasoningStartUIPart { Id = currentStreamId };
                                reasoningStarted = true;
                            }

                            yield return new ReasoningDeltaUIPart
                            {
                                Id = currentStreamId,
                                Delta = deltaThinking
                            };
                        }
                        break;
                    }

                case "content-end":
                    {
                        if (textStarted && currentStreamId is not null)
                        {
                            yield return currentStreamId.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }

                        if (reasoningStarted && currentStreamId is not null)
                        {
                            yield return new ReasoningEndUIPart { Id = currentStreamId };
                            reasoningStarted = false;
                        }
                        break;
                    }

                case "citation-start":
                    {
                        // Optioneel: je kunt hier SourceUIPart maken als url/meta beschikbaar is
                        break;
                    }

                case "citation-end":
                    {
                        // idem
                        break;
                    }

                case "message-end":
                    {
                        sawMessageEnd = true;

                        // Finish + usage
                        var finishReason = dataNode?["delta"]?["finish_reason"]?.GetValue<string>().ToFinishReason()
                                          ?? dataNode?["finish_reason"]?.GetValue<string>().ToFinishReason()
                                          ?? "stop";

                        // Try multiple shapes for usage
                        var usage = dataNode?["delta"]?["usage"]
                                 ?? dataNode?["usage"];

                        // Cohere examples show:
                        // usage.tokens.input_tokens / output_tokens
                        inputTokens = usage?["tokens"]?["input_tokens"]?.GetValue<int?>() ?? inputTokens;
                        outputTokens = usage?["tokens"]?["output_tokens"]?.GetValue<int?>() ?? outputTokens;
                        totalTokens = (inputTokens >= 0 && outputTokens >= 0)
                                        ? inputTokens + outputTokens
                                        : usage?["tokens"]?["total_tokens"]?.GetValue<int?>() ?? totalTokens;

                        // Some responses nest "billed_units" but we prefer tokens above.

                        // Close open streams
                        if (textStarted && currentStreamId is not null)
                        {
                            yield return currentStreamId.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }
                        if (reasoningStarted && currentStreamId is not null)
                        {
                            yield return new ReasoningEndUIPart { Id = currentStreamId };
                            reasoningStarted = false;
                        }

                        yield return (finishReason ?? "stop").ToFinishUIPart(
                            modelId,
                            outputTokens,
                            inputTokens,
                            totalTokens,
                            chatRequest.Temperature,
                            reasoningTokens: reasoningTokens,
                            extraMetadata: null
                        );
                        break;
                    }

                case "debug":
                default:
                    // Negeer overige events of gebruik voor logging
                    break;
            }
        }

        // Safety net als stream eindigt zonder message-end
        if (!sawMessageEnd)
        {
            if (textStarted && currentStreamId is not null)
                yield return currentStreamId.ToTextEndUIMessageStreamPart();
            if (reasoningStarted && currentStreamId is not null)
                yield return new ReasoningEndUIPart { Id = currentStreamId };

            yield return "stop".ToFinishUIPart(
                modelId,
                outputTokens,
                inputTokens,
                totalTokens,
                chatRequest.Temperature,
                reasoningTokens: reasoningTokens,
                extraMetadata: null
            );
        }
    }
}
