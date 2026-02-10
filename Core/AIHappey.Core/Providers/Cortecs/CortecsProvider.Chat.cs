using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Cortecs;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);
        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<CortecsProviderMetadata>(GetIdentifier());
        var payload = BuildCortecsUiStreamPayload(chatRequest, metadata);
        var reqJson = JsonSerializer.Serialize(payload, JsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Cortecs stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? id = null;
        bool textStarted = false;
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;

        var toolBuffers = new Dictionary<string, (string? ToolName, StringBuilder Args, int Index)>();
        var indexToKey = new Dictionary<int, string>();
        var toolStartSent = new HashSet<string>();

        static bool LooksLikeJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s is "\"" or "''") return false;
            return (s.StartsWith("{") && s.EndsWith("}")) || (s.StartsWith("[") && s.EndsWith("]"));
        }

        string ResolveKey(string? idPart, int index)
        {
            if (!string.IsNullOrWhiteSpace(idPart)) return idPart!;
            if (index >= 0 && indexToKey.TryGetValue(index, out var existing)) return existing;
            return $"idx:{index}";
        }

        void RememberAlias(string key, int index)
        {
            if (index >= 0 && !indexToKey.ContainsKey(index))
                indexToKey[index] = key;
        }

        async IAsyncEnumerable<UIMessagePart> FlushToolCalls()
        {
            foreach (var kv in toolBuffers.OrderBy(kv => kv.Value.Index).ToArray())
            {
                var key = kv.Key;
                var (toolName, sb, _) = kv.Value;

                if (string.IsNullOrWhiteSpace(toolName))
                    continue;

                var raw = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw) || raw is "\"" or "''")
                    continue;

                object inputObj;
                try
                {
                    inputObj = LooksLikeJson(raw)
                        ? JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web)!
                        : new { value = raw };
                }
                catch (JsonException)
                {
                    continue;
                }

                yield return new ToolCallPart
                {
                    ToolCallId = key,
                    ToolName = toolName!,
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
                foreach (var idx in toRemove) indexToKey.Remove(idx);
            }
        }

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0) continue;
            if (line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data)) continue;
            if (data is "[DONE]" or "[done]") break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            id ??= root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : Guid.NewGuid().ToString("n");

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

                if (delta.TryGetProperty("content", out var content))
                {
                    var deltaText = content.ExtractCortecsText();
                    if (!string.IsNullOrWhiteSpace(deltaText))
                    {
                        if (!textStarted)
                        {
                            yield return id!.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart { Id = id!, Delta = deltaText };
                    }
                }

                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCalls.EnumerateArray())
                    {
                        string? idPart = tc.TryGetProperty("id", out var tcIdEl) && tcIdEl.ValueKind == JsonValueKind.String
                            ? tcIdEl.GetString()
                            : null;

                        var index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                            ? idxEl.GetInt32()
                            : -1;

                        if (!tc.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object)
                            continue;

                        var key = ResolveKey(idPart, index);
                        if (!string.IsNullOrWhiteSpace(idPart)) RememberAlias(key, index);

                        if (!toolBuffers.TryGetValue(key, out var buf))
                            buf = (ToolName: null, Args: new StringBuilder(), Index: index);

                        if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                        {
                            buf.ToolName = nameEl.GetString();
                            toolBuffers[key] = buf;

                            if (!string.IsNullOrWhiteSpace(buf.ToolName) && !toolStartSent.Contains(key))
                            {
                                yield return new ToolCallStreamingStartPart
                                {
                                    ToolCallId = key,
                                    ToolName = buf.ToolName!,
                                    Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == buf.ToolName)?.Title,
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

                                yield return new ToolCallDeltaPart
                                {
                                    ToolCallId = key,
                                    InputTextDelta = frag
                                };
                            }
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    var finish = fr.GetString() ?? "stop";

                    if (finish is "tool_calls" or "stop")
                    {
                        await foreach (var part in FlushToolCalls())
                            yield return part;
                    }

                    if (textStarted)
                    {
                        yield return id!.ToTextEndUIMessageStreamPart();
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

        if (toolBuffers.Count > 0)
        {
            await foreach (var part in FlushToolCalls())
                yield return part;
        }

        if (textStarted && id is not null)
            yield return id.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

    private static object BuildCortecsUiStreamPayload(ChatRequest chatRequest, CortecsProviderMetadata? metadata)
    {
        var tools = chatRequest.Tools is { Count: > 0 }
            ? chatRequest.Tools.ToCortecsTools().Cast<object>().ToList()
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["stream"] = true,
            ["messages"] = chatRequest.Messages.ToCortecsMessages().ToList(),
            ["temperature"] = chatRequest.Temperature,
            ["top_p"] = chatRequest.TopP,
            ["max_tokens"] = chatRequest.MaxOutputTokens,
            ["response_format"] = chatRequest.ResponseFormat,
            ["tool_choice"] = chatRequest.ToolChoice,
            ["tools"] = tools,
            ["preference"] = metadata?.Preference,
            ["allowed_providers"] = metadata?.AllowedProviders,
            ["eu_native"] = metadata?.EuNative,
            ["allow_quantization"] = metadata?.AllowQuantization,
            ["safe_prompt"] = metadata?.SafePrompt,
            ["frequency_penalty"] = metadata?.FrequencyPenalty,
            ["presence_penalty"] = metadata?.PresencePenalty,
            ["stop"] = metadata?.Stop,
            ["seed"] = metadata?.Seed,
            ["n"] = metadata?.N,
            ["logprobs"] = metadata?.LogProbs,
            ["prediction"] = metadata?.Prediction
        };

        return payload;
    }
}
