using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using System.Text;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var options = BuildChatOptionsFromChatRequest(chatRequest, stream: true);

        await foreach (var part in StreamUiPartsInternalAsync(chatRequest, options, cancellationToken))
            yield return part;
    }

    private async IAsyncEnumerable<UIMessagePart> StreamUiPartsInternalAsync(
    ChatRequest chatRequest,
    ChatCompletionOptions options,
    [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fullText = new StringBuilder();

        string streamId = Guid.NewGuid().ToString("n");
        string modelId = chatRequest.Model;
        string finishReason = "stop";

        bool textStarted = false;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;

        var toolBuffers = new Dictionary<string, ToolBufferState>();
        var indexToKey = new Dictionary<int, string>();
        var toolStartSent = new HashSet<string>();

        static string ResolveToolKey(string? idPart, int index, IReadOnlyDictionary<int, string> aliases)
        {
            if (!string.IsNullOrWhiteSpace(idPart))
                return idPart;

            if (index >= 0 && aliases.TryGetValue(index, out var key))
                return key;

            return $"idx:{index}";
        }

        IEnumerable<UIMessagePart> FlushToolCalls()
        {
            foreach (var kv in toolBuffers.OrderBy(a => a.Value.Index).ToArray())
            {
                var key = kv.Key;
                var state = kv.Value;

                if (string.IsNullOrWhiteSpace(state.ToolName))
                    continue;

                var raw = state.Args.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    raw = "{}";

                object input;
                try
                {
                    input = JsonSerializer.Deserialize<object>(raw, Json) ?? new { };
                }
                catch
                {
                    input = new { value = raw };
                }

                var providerExecuted = state.ProviderExecuted;

                yield return new ToolCallPart
                {
                    ToolCallId = key,
                    ToolName = state.ToolName!,
                    Title = chatRequest.Tools?.FirstOrDefault(t => t.Name == state.ToolName)?.Title,
                    ProviderExecuted = providerExecuted,
                    Input = input
                };

                if (providerExecuted)
                {
                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = key,
                        ProviderExecuted = true,
                        Output = new ModelContextProtocol.Protocol.CallToolResult
                        {
                            IsError = false,
                            Content =
                            [
                                "Provider-side tool execution completed. Parallel does not expose full tool output payload in this stream."
                                    .ToTextContentBlock()
                            ]
                        }
                    };
                }
                else
                {
                    yield return new ToolApprovalRequestPart
                    {
                        ToolCallId = key,
                        ApprovalId = Guid.NewGuid().ToString("N")
                    };
                }

                toolBuffers.Remove(key);
                toolStartSent.Remove(key);
                foreach (var alias in indexToKey.Where(a => a.Value == key).Select(a => a.Key).ToList())
                    indexToKey.Remove(alias);
            }
        }

        await foreach (var raw in StreamChatRawChunksAsync(options, cancellationToken))
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                streamId = idEl.GetString() ?? streamId;

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                modelId = modelEl.GetString() ?? modelId;

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();
                if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    completionTokens = ct.GetInt32();
                if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    totalTokens = tt.GetInt32();
            }

            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var choice in choicesEl.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.Object)
                {
                    if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var delta = contentEl.GetString();
                        if (!string.IsNullOrEmpty(delta))
                        {
                            if (!textStarted)
                            {
                                yield return streamId.ToTextStartUIMessageStreamPart();
                                textStarted = true;
                            }

                            fullText.Append(delta);
                            yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = delta };
                        }
                    }

                    var choiceProviderExecuted = deltaEl.TryGetProperty("role", out var roleEl)
                                               && roleEl.ValueKind == JsonValueKind.String
                                               && string.Equals(roleEl.GetString(), "tool", StringComparison.OrdinalIgnoreCase);

                    if (deltaEl.TryGetProperty("tool_calls", out var toolCallsEl) && toolCallsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsEl.EnumerateArray())
                        {
                            var idPart = tc.TryGetProperty("id", out var tcIdEl) && tcIdEl.ValueKind == JsonValueKind.String
                                ? tcIdEl.GetString()
                                : null;

                            var index = tc.TryGetProperty("index", out var indexEl) && indexEl.ValueKind == JsonValueKind.Number
                                ? indexEl.GetInt32()
                                : -1;

                            var key = ResolveToolKey(idPart, index, indexToKey);
                            if (!string.IsNullOrWhiteSpace(idPart) && index >= 0)
                                indexToKey[index] = key;

                            if (!toolBuffers.TryGetValue(key, out var state))
                                state = new ToolBufferState(index);

                            state.ProviderExecuted |= choiceProviderExecuted;

                            if (tc.TryGetProperty("function", out var functionEl) && functionEl.ValueKind == JsonValueKind.Object)
                            {
                                if (functionEl.TryGetProperty("name", out var fnNameEl) && fnNameEl.ValueKind == JsonValueKind.String)
                                {
                                    state.ToolName = fnNameEl.GetString();

                                    if (!string.IsNullOrWhiteSpace(state.ToolName) && !toolStartSent.Contains(key))
                                    {
                                        yield return new ToolCallStreamingStartPart
                                        {
                                            ToolCallId = key,
                                            ToolName = state.ToolName!,
                                            Title = chatRequest.Tools?.FirstOrDefault(t => t.Name == state.ToolName)?.Title,
                                            ProviderExecuted = state.ProviderExecuted
                                        };
                                        toolStartSent.Add(key);
                                    }
                                }

                                if (functionEl.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                                {
                                    var frag = argsEl.GetString();
                                    if (!string.IsNullOrEmpty(frag))
                                    {
                                        state.Args.Append(frag);
                                        yield return new ToolCallDeltaPart
                                        {
                                            ToolCallId = key,
                                            InputTextDelta = frag
                                        };
                                    }
                                }
                            }

                            toolBuffers[key] = state;
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var finishEl) && finishEl.ValueKind == JsonValueKind.String)
                {
                    finishReason = finishEl.GetString() ?? finishReason;
                    if (finishReason is "tool_calls" or "function_call" or "stop")
                    {
                        foreach (var part in FlushToolCalls())
                            yield return part;
                    }
                }
            }
        }

        foreach (var part in FlushToolCalls())
            yield return part;

        if (textStarted)
            yield return streamId.ToTextEndUIMessageStreamPart();

        if (chatRequest.ResponseFormat != null && fullText.Length > 0)
        {

            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            var data = JsonSerializer.Deserialize<object>(fullText.ToString(), Json);
            if (data is not null)
            {
                yield return new DataUIPart
                {
                    Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                    Data = data
                };
            }

        }

        yield return finishReason.ToFinishUIPart(
            modelId,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

}
