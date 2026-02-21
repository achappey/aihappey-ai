using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model.Providers.Infomaniak;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    private async Task<ChatCompletion> CompleteChatCustomAsync(
        ChatCompletionOptions options,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var payload = BuildInfomaniakChatPayload(options, stream: false);
        var reqJson = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Infomaniak chat error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return ParseChatCompletion(doc.RootElement, options.Model);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingCustomAsync(
        ChatCompletionOptions options,
        string relativeUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildInfomaniakChatPayload(options, stream: true);
        var reqJson = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Infomaniak stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0) continue;
            if (data is "[DONE]" or "[done]") yield break;

            ChatCompletionUpdate? update = TryDeserializeChatCompletionUpdate(data);
            if (update is not null)
                yield return update;
        }
    }

    private async IAsyncEnumerable<UIMessagePart> StreamCustomAsync(
        ChatRequest chatRequest,
        string relativeUrl,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var metadata = chatRequest.GetProviderMetadata<InfomaniakProviderMetadata>(GetIdentifier());
        var payload = BuildInfomaniakUiStreamPayload(chatRequest, metadata);
        var reqJson = JsonSerializer.Serialize(payload, Json);

        using var req = new HttpRequestMessage(HttpMethod.Post, relativeUrl)
        {
            Content = new StringContent(reqJson, Encoding.UTF8, "application/json")
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"Infomaniak stream error: {(string.IsNullOrWhiteSpace(err) ? resp.ReasonPhrase : err)}".ToErrorUIPart();
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

        string ResolveKey(string? idPart, int index)
        {
            if (!string.IsNullOrWhiteSpace(idPart)) return idPart!;
            if (index >= 0 && indexToKey.TryGetValue(index, out var key)) return key;
            return $"idx:{index}";
        }

        void RememberAlias(string key, int index)
        {
            if (index >= 0 && !indexToKey.ContainsKey(index))
                indexToKey[index] = key;
        }

        static bool LooksLikeJson(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            return (s.StartsWith("{") && s.EndsWith("}")) || (s.StartsWith("[") && s.EndsWith("]"));
        }

        async IAsyncEnumerable<UIMessagePart> FlushToolCalls()
        {
            foreach (var kv in toolBuffers.OrderBy(a => a.Value.Index).ToArray())
            {
                var key = kv.Key;
                var (toolName, args, _) = kv.Value;

                if (string.IsNullOrWhiteSpace(toolName))
                    continue;

                var raw = args.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw) || raw is "\"" or "''")
                    continue;

                object? input;
                try
                {
                    input = LooksLikeJson(raw)
                        ? JsonSerializer.Deserialize<object>(raw, Json)
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
                    Input = input ?? new { }
                };

                yield return new ToolApprovalRequestPart
                {
                    ToolCallId = key,
                    ApprovalId = Guid.NewGuid().ToString()
                };

                toolBuffers.Remove(key);
                toolStartSent.Remove(key);

                var aliases = indexToKey.Where(p => p.Value == key).Select(p => p.Key).ToList();
                foreach (var alias in aliases) indexToKey.Remove(alias);
            }
        }

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (line.Length == 0 || line.StartsWith(':')) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data)) continue;
            if (data is "[DONE]" or "[done]") break;

            using var doc = JsonDocument.Parse(data);
            var root = ResolveSseRoot(doc.RootElement);

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
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (!textStarted)
                            {
                                yield return id!.ToTextStartUIMessageStreamPart();
                                textStarted = true;
                            }

                            yield return new TextDeltaUIMessageStreamPart
                            {
                                Id = id!,
                                Delta = text
                            };
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

    private static object BuildInfomaniakChatPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = options.Messages.Select(MapChatMessage).ToList(),
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["response_format"] = options.ResponseFormat,
            ["parallel_tool_calls"] = options.ParallelToolCalls,
            ["tool_choice"] = options.ToolChoice,
            ["tools"] = options.Tools?.Any() == true ? options.Tools.ToList() : null
        };

        if (stream)
            payload["stream_options"] = new { include_usage = true };

        var root = JsonSerializer.SerializeToElement(options, Json);

        AddIfPresent(root, payload, "max_completion_tokens");
        AddIfPresent(root, payload, "reasoning_effort");
        AddIfPresent(root, payload, "presence_penalty");
        AddIfPresent(root, payload, "top_p");
        AddIfPresent(root, payload, "stop");
        AddIfPresent(root, payload, "seed");
        AddIfPresent(root, payload, "n");
        AddIfPresent(root, payload, "logprobs");
        AddIfPresent(root, payload, "top_logprobs");
        AddIfPresent(root, payload, "logit_bias");

        return payload;
    }

    private static object BuildInfomaniakUiStreamPayload(ChatRequest chatRequest, InfomaniakProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["messages"] = MapUiMessages(chatRequest.Messages).ToList(),
            ["stream"] = true,
            ["stream_options"] = new
            {
                include_usage = metadata?.StreamOptionsIncludeUsage ?? true,
                include_obfuscation = metadata?.StreamOptionsIncludeObfuscation
            },
            ["temperature"] = chatRequest.Temperature,
            ["top_p"] = chatRequest.TopP,
            ["max_completion_tokens"] = chatRequest.MaxOutputTokens,
            ["tool_choice"] = chatRequest.ToolChoice,
            ["parallel_tool_calls"] = metadata?.ParallelToolCalls,
            ["reasoning_effort"] = metadata?.ReasoningEffort,
            ["presence_penalty"] = metadata?.PresencePenalty,
            ["stop"] = metadata?.Stop,
            ["seed"] = metadata?.Seed,
            ["n"] = metadata?.N,
            ["logprobs"] = metadata?.LogProbs,
            ["top_logprobs"] = metadata?.TopLogProbs,
            ["logit_bias"] = metadata?.LogitBias
        };

        var tools = MapTools(chatRequest.Tools);
        if (tools.Count > 0)
            payload["tools"] = tools;

        return payload;
    }

    private static IEnumerable<object> MapUiMessages(IEnumerable<UIMessage> uiMessages)
    {
        foreach (var msg in uiMessages)
        {
            var role = msg.Role.ToString().ToLowerInvariant();

            if (msg.Role is Role.user or Role.system)
            {
                var parts = new List<object>();
                foreach (var part in msg.Parts)
                {
                    if (part is TextUIPart text && !string.IsNullOrWhiteSpace(text.Text))
                        parts.Add(new { type = "text", text = text.Text });

                    if (part is FileUIPart file && file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.Url))
                        parts.Add(new { type = "image_url", image_url = new { url = file.Url } });
                }

                if (parts.Count > 0)
                    yield return new { role, content = parts };

                continue;
            }

            if (msg.Role == Role.assistant)
            {
                var bufferedContent = new List<object>();

                foreach (var part in msg.Parts)
                {
                    if (part is TextUIPart text && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        bufferedContent.Add(new { type = "text", text = text.Text });
                        continue;
                    }

                    if (part is ToolInvocationPart tip)
                    {
                        if (bufferedContent.Count > 0)
                        {
                            yield return new { role = "assistant", content = bufferedContent.ToArray() };
                            bufferedContent.Clear();
                        }

                        var toolName = tip.GetToolName();
                        var argsJson = tip.Input is null ? "{}" : JsonSerializer.Serialize(tip.Input, Json);

                        var toolCall = new
                        {
                            id = tip.ToolCallId,
                            type = "function",
                            function = new { name = toolName, arguments = argsJson }
                        };

                        yield return new
                        {
                            role = "assistant",
                            content = (object?)null,
                            tool_calls = new[] { toolCall }
                        };

                        if (tip.Output is not null)
                        {
                            yield return new
                            {
                                role = "tool",
                                tool_call_id = tip.ToolCallId,
                                name = toolName,
                                content = tip.Output is string s ? s : JsonSerializer.Serialize(tip.Output, Json)
                            };
                        }
                    }
                }

                if (bufferedContent.Count > 0)
                    yield return new { role = "assistant", content = bufferedContent.ToArray() };
            }
        }
    }

    private static List<object> MapTools(List<Tool>? tools)
    {
        if (tools is null || tools.Count == 0)
            return [];

        return
        [
            .. tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            })
        ];
    }

    private static object MapChatMessage(ChatMessage msg)
    {
        var result = new Dictionary<string, object?>
        {
            ["role"] = msg.Role,
            ["content"] = JsonSerializer.Deserialize<object>(msg.Content.GetRawText(), Json)
        };

        if (!string.IsNullOrWhiteSpace(msg.ToolCallId))
            result["tool_call_id"] = msg.ToolCallId;

        if (msg.ToolCalls?.Any() == true)
            result["tool_calls"] = msg.ToolCalls;

        return result;
    }

    private static ChatCompletion ParseChatCompletion(JsonElement root, string fallbackModel)
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : Guid.NewGuid().ToString("n");

        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : fallbackModel;

        var created = root.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number && createdEl.TryGetInt64(out var epoch)
            ? epoch
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var objectType = root.TryGetProperty("object", out var objectEl) && objectEl.ValueKind == JsonValueKind.String
            ? objectEl.GetString()
            : "chat.completion";

        object[] choices = [];
        if (root.TryGetProperty("choices", out var choicesEl))
        {
            if (choicesEl.ValueKind == JsonValueKind.Array)
                choices = JsonSerializer.Deserialize<object[]>(choicesEl.GetRawText(), Json) ?? [];
            else if (choicesEl.ValueKind == JsonValueKind.Object)
                choices = [JsonSerializer.Deserialize<object>(choicesEl.GetRawText(), Json)!];
        }

        object? usage = null;
        if (root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            usage = JsonSerializer.Deserialize<object>(usageEl.GetRawText(), Json);
        }

        return new ChatCompletion
        {
            Id = id!,
            Object = objectType!,
            Created = created,
            Model = model ?? fallbackModel,
            Choices = choices,
            Usage = usage
        };
    }

    private static ChatCompletionUpdate? TryDeserializeChatCompletionUpdate(string data)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, Json);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Id))
                return parsed;
        }
        catch
        {
            // no-op; try fallback shape
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = ResolveSseRoot(doc.RootElement);
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return JsonSerializer.Deserialize<ChatCompletionUpdate>(root.GetRawText(), Json);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement ResolveSseRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var inner)
            && inner.ValueKind == JsonValueKind.Object)
        {
            return inner;
        }

        return root;
    }

    private static void AddIfPresent(JsonElement root, IDictionary<string, object?> payload, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return;

        if (prop.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        payload[propertyName] = JsonSerializer.Deserialize<object>(prop.GetRawText(), Json);
    }
}

