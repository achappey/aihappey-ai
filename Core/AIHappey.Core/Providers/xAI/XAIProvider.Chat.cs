using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = chatRequest.BuildXAIStreamRequest(GetIdentifier());

        // ---------- 2) Send and read SSE ----------
        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? currentStreamId = null;
        string modelId = chatRequest.Model;
        bool sawCompleted = false;

        bool starterSend = false;
        bool reasoningStarterSend = false;
        int inputTokens = 0, outputTokens = 0, totalTokens = 0;
        int? reasoningTokens = null;
        StringBuilder fullMessageText = new();

        // SSE parser state
        string? eventType = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;

            if (line.Length == 0) { eventType = null; continue; }        // blank line = event separator
            if (line.StartsWith(':')) continue;                          // comment line
            if (line.StartsWith("event: "))
            {
                eventType = line["event: ".Length..].Trim();
                continue;
            }

            if (!line.StartsWith("data: ")) continue;

            var dataStr = line["data: ".Length..].Trim();

            if (dataStr == "[DONE]" || dataStr == "[done]")
                break;

            using JsonDocument doc = JsonDocument.Parse(dataStr);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                modelId = mEl.GetString() ?? modelId;

            if (root.TryGetProperty("response", out var respEl) && respEl.ValueKind == JsonValueKind.Object)
            {
                // Also pick up model here if you want:
                if (respEl.TryGetProperty("model", out var m2) && m2.ValueKind == JsonValueKind.String)
                    modelId = m2.GetString() ?? modelId;

                if (respEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    currentStreamId ??= idEl.GetString();

                ReadUsage(respEl, ref inputTokens, ref outputTokens, ref totalTokens, ref reasoningTokens);
            }

            bool clientToolsCalls = false;
            // ---------- 3) Handle key Responses events ----------
            switch (eventType)
            {
                case "response.output_text.annotation_added":
                    {
                        if (root.TryGetProperty("annotation", out var annotationEl)
                            && annotationEl.ValueKind == JsonValueKind.Object)
                        {
                            string? type = null;
                            if (annotationEl.TryGetProperty("type", out var typeEl)
                                && typeEl.ValueKind == JsonValueKind.String)
                                type = typeEl.GetString();

                            // Handle URL citation
                            if (string.Equals(type, "url_citation", StringComparison.OrdinalIgnoreCase)
                                && annotationEl.TryGetProperty("url", out var urlEl)
                                && urlEl.ValueKind == JsonValueKind.String)
                            {
                                var url = urlEl.GetString();
                                if (!string.IsNullOrWhiteSpace(url))
                                {
                                    string? title = null;
                                    if (annotationEl.TryGetProperty("title", out var titleEl)
                                        && titleEl.ValueKind == JsonValueKind.String)
                                        title = titleEl.GetString();

                                    yield return new SourceUIPart
                                    {
                                        Url = url!,
                                        Title = string.IsNullOrWhiteSpace(title) ? null : title,
                                        SourceId = url!,
                                    };
                                }
                            }
                        }
                        break;
                    }
                // Text deltas arrive token-by-token
                case "response.output_text.delta":
                case "response.text.delta": // alias some SDKs use
                    {
                        // payload often shapes like { "delta": "text..." } or { "output_text": { "delta": "..." }}
                        string? delta = TryGetDeltaText(root);
                        if (!string.IsNullOrEmpty(delta))
                        {
                            if (currentStreamId is not null)
                            {
                                if (!starterSend)
                                {
                                    yield return currentStreamId.ToTextStartUIMessageStreamPart();
                                    starterSend = true;
                                }

                                fullMessageText.Append(delta);

                                yield return new TextDeltaUIMessageStreamPart
                                {
                                    Id = currentStreamId,
                                    Delta = delta
                                };
                            }
                        }
                        break;
                    }

                case "response.reasoning_summary_text.delta":
                    //   case "response.reasoning_text.delta":
                    {
                        // payload often shapes like { "delta": "text..." } or { "output_text": { "delta": "..." }}
                        string? delta = TryGetDeltaText(root);
                        if (!string.IsNullOrEmpty(delta))
                        {
                            if (currentStreamId is not null)
                            {
                                if (!reasoningStarterSend)
                                {
                                    yield return new ReasoningStartUIPart { Id = currentStreamId };
                                    reasoningStarterSend = true;
                                }

                                yield return new ReasoningDeltaUIPart
                                {
                                    Id = currentStreamId,
                                    Delta = delta
                                };
                            }
                        }
                        break;
                    }

                case "response.reasoning_summary_text.done":
                    {
                        if (currentStreamId is not null)
                        {
                            yield return new ReasoningEndUIPart
                            {
                                Id = currentStreamId
                            };

                            reasoningStarterSend = false;
                        }
                        //  }
                        break;
                    }

                case "response.output_item.done":
                    {
                        var providerExecuted = !(chatRequest.Tools?.Count > 0);

                        foreach (var part in ReadToolCallPart(root, providerExecuted, chatRequest.Tools ?? []))
                        {
                            clientToolsCalls = !providerExecuted;
                            yield return part;
                        }

                        break;
                    }
                // Final completion event for the whole response
                case "response.completed":
                case "response.completed.success":
                    {
                        sawCompleted = true;
                        // --- 1️⃣ Extract citations ---

                        if (currentStreamId is not null && starterSend)
                        {
                            // If caller didn’t emit output_text.done, ensure we end the text stream
                            yield return new TextEndUIMessageStreamPart { Id = currentStreamId };
                            currentStreamId = null;
                            starterSend = false;
                        }

                        if (chatRequest.ResponseFormat != null)
                        {
                            var fullMessage = fullMessageText.ToString();

                            if (!string.IsNullOrEmpty(fullMessage))
                            {
                                var schema = chatRequest.ResponseFormat.GetJSONSchema();
                                var dataObject = JsonSerializer.Deserialize<object>(fullMessage);

                                if (dataObject != null)
                                    yield return new DataUIPart()
                                    {
                                        Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                                        Data = dataObject
                                    };
                            }
                        }

                        yield return "stop"
                        .ToFinishUIPart(
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

                // Error surfaced as event
                case "response.error":
                    {
                        var msg = root.TryGetProperty("error", out var eEl) && eEl.TryGetProperty("message", out var m)
                            ? m.GetString()
                            : "Unknown error";

                        yield return new ErrorUIPart()
                        {
                            ErrorText = $"xAI stream error: {msg}"
                        };
                        break;
                    }

                // You can extend for function/tool call deltas if you wire tools
                default:
                    // Ignore other events (tool calls, annotations, etc.) for now
                    break;
            }
        }

        // If the connection ended without an explicit completed event, still emit finish
        if (!sawCompleted)
        {
            if (currentStreamId is not null)
                yield return new TextEndUIMessageStreamPart { Id = currentStreamId };

            if (chatRequest.ResponseFormat != null)
            {
                var fullMessage = fullMessageText.ToString();

                if (!string.IsNullOrEmpty(fullMessage))
                {
                    var schema = chatRequest.ResponseFormat.GetJSONSchema();
                    var dataObject = JsonSerializer.Deserialize<object>(fullMessage);

                    if (dataObject != null)
                        yield return new DataUIPart()
                        {
                            Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                            Data = dataObject
                        };
                }
            }

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