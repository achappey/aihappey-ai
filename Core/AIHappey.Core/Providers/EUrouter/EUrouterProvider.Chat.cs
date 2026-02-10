using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(chatRequest);

        var (provider, model) = ResolveProviderAndModel(chatRequest.Model);
        var messages = ToEurouterMessages(chatRequest.Messages).ToList();
        var tools = chatRequest.Tools?.Any() == true
            ? chatRequest.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            }).ToList<object>()
            : [];

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["temperature"] = chatRequest.Temperature,
            ["top_p"] = chatRequest.TopP,
            ["max_tokens"] = chatRequest.MaxOutputTokens,
            ["tools"] = tools,
            ["tool_choice"] = chatRequest.ToolChoice
                ?? (tools?.Count > 0 ? "auto" : "none"),
            ["response_format"] = chatRequest.ResponseFormat ?? new { type = "text" }
        };

        if (!string.IsNullOrWhiteSpace(provider))
        {
            payload["provider"] = new
            {
                order = new List<string>() { provider! },
                allow_fallbacks = false
            };
        }

        var json = JsonSerializer.Serialize(payload, EurouterJsonOptions);

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
            yield return $"EUrouter stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? id = null;
        bool started = false;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;

        var toolBuffers = new Dictionary<string, (string? ToolName, StringBuilder Args, int Index)>();
        var indexToKey = new Dictionary<int, string>();
        var toolStartSent = new HashSet<string>();
        var fullMessageText = new StringBuilder();

        string ResolveKey(string? idPart, int index)
        {
            if (!string.IsNullOrEmpty(idPart)) return idPart;
            if (index >= 0 && indexToKey.TryGetValue(index, out var k)) return k;
            return $"idx:{index}";
        }

        void RememberAlias(string key, int index)
        {
            if (index >= 0 && !indexToKey.ContainsKey(index))
                indexToKey[index] = key;
        }

        bool LooksLikeJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return (s.StartsWith("{") && s.EndsWith("}")) || (s.StartsWith("[") && s.EndsWith("]"));
        }

        async IAsyncEnumerable<UIMessagePart> FlushToolCalls()
        {
            foreach (var kv in toolBuffers.OrderBy(kv => kv.Value.Index).ToArray())
            {
                var key = kv.Key;
                var (toolName, sb, _) = kv.Value;

                if (string.IsNullOrEmpty(toolName))
                    continue;

                var raw = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                object inputObj;
                try
                {
                    inputObj = LooksLikeJson(raw)
                        ? JsonSerializer.Deserialize<object>(raw, EurouterJsonOptions)!
                        : new { value = raw };
                }
                catch (JsonException)
                {
                    continue;
                }

                yield return new ToolCallPart
                {
                    ToolCallId = key,
                    ToolName = toolName,
                    Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
                    ProviderExecuted = false,
                    Input = inputObj
                };

                yield return new ToolApprovalRequestPart
                {
                    ToolCallId = key,
                    ApprovalId = Guid.NewGuid().ToString()
                };

                toolBuffers.Remove(key);
                toolStartSent.Remove(key);

                var toRemove = indexToKey.Where(p => p.Value == key).Select(p => p.Key).ToList();
                foreach (var r in toRemove) indexToKey.Remove(r);
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(":")) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data is "[DONE]" or "[done]") break;
            if (string.IsNullOrWhiteSpace(data)) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    completionTokens = ct.GetInt32();
                if (usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    totalTokens = tt.GetInt32();
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                    continue;

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

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        string? idPart = null;
                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            idPart = idEl.GetString();

                        var index = -1;
                        if (tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number)
                            index = idxEl.GetInt32();

                        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                            continue;

                        var key = ResolveKey(idPart, index);
                        if (!string.IsNullOrEmpty(idPart)) RememberAlias(key, index);

                        if (!toolBuffers.TryGetValue(key, out var buf))
                            buf = (ToolName: null, Args: new StringBuilder(), Index: index);

                        if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        {
                            buf.ToolName = nameEl.GetString();
                            toolBuffers[key] = buf;

                            if (!string.IsNullOrEmpty(buf.ToolName) && !toolStartSent.Contains(key))
                            {
                                yield return new ToolCallStreamingStartPart
                                {
                                    ToolCallId = key,
                                    Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == buf.ToolName)?.Title,
                                    ToolName = buf.ToolName,
                                    ProviderExecuted = false
                                };
                                toolStartSent.Add(key);
                            }
                        }
                        else
                        {
                            toolBuffers[key] = buf;
                        }

                        if (fn.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                        {
                            var frag = argsEl.GetString();
                            if (!string.IsNullOrEmpty(frag))
                            {
                                buf = toolBuffers[key];
                                buf.Args.Append(frag);
                                toolBuffers[key] = buf;
                            }
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    var reason = fr.GetString();
                    if (reason is "tool_calls" or "stop")
                    {
                        await foreach (var part in FlushToolCalls())
                            yield return part;
                    }
                }
            }
        }

        if (toolBuffers.Count > 0)
        {
            await foreach (var part in FlushToolCalls())
                yield return part;
        }

        if (started && id is not null)
            yield return id.ToTextEndUIMessageStreamPart();

        if (chatRequest.ResponseFormat != null)
        {
            var fullMessage = fullMessageText.ToString();
            if (!string.IsNullOrWhiteSpace(fullMessage))
            {
                var schema = chatRequest.ResponseFormat.GetJSONSchema();
                var dataObject = JsonSerializer.Deserialize<object>(fullMessage);
                if (dataObject is not null)
                {
                    yield return new DataUIPart
                    {
                        Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                        Data = dataObject
                    };
                }
            }
        }

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

    private static IEnumerable<object> ToEurouterMessages(IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            switch (msg.Role)
            {
                case Role.system:
                case Role.user:
                    {
                        var role = msg.Role == Role.system ? "system" : "user";
                        var content = string.Join("\n", msg.Parts.OfType<TextUIPart>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                        yield return new { role, content = content ?? string.Empty };
                        break;
                    }

                case Role.assistant:
                    {
                        var assistantText = string.Join("\n", msg.Parts.OfType<TextUIPart>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                        if (!string.IsNullOrWhiteSpace(assistantText))
                            yield return new { role = "assistant", content = assistantText };

                        foreach (var tip in msg.Parts.OfType<ToolInvocationPart>())
                        {
                            var toolName = tip.GetToolName();
                            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(tip.ToolCallId))
                                continue;

                            var argsJson = tip.Input is null ? "{}" : JsonSerializer.Serialize(tip.Input, JsonSerializerOptions.Web);

                            yield return new
                            {
                                role = "assistant",
                                content = string.Empty,
                                tool_calls = new[]
                                {
                                new
                                {
                                    id = tip.ToolCallId,
                                    type = "function",
                                    function = new { name = toolName, arguments = argsJson }
                                }
                            }
                            };

                            if (tip.Output is not null)
                            {
                                var output = tip.Output is string s
                                    ? s
                                    : JsonSerializer.Serialize(tip.Output, JsonSerializerOptions.Web);

                                yield return new
                                {
                                    role = "tool",
                                    tool_call_id = tip.ToolCallId,
                                    content = output
                                };
                            }
                        }

                        break;
                    }

                default:
                    {
                        var fallback = string.Join("\n", msg.Parts.OfType<TextUIPart>().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                        yield return new { role = "user", content = fallback ?? string.Empty };
                        break;
                    }
            }
        }
    }
}
