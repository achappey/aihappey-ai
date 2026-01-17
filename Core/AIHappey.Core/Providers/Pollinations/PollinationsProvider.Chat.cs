using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text;
using System.Net.Mime;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Pollinations;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider : IModelProvider
{
    public Task<string> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<string> GetToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model));
        if (model?.Type == "image")
        {
            await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        if (await IsPollinationsImageModel(chatRequest.Model, cancellationToken))
        {
            await foreach (var p in StreamImageAsync(chatRequest, chatRequest.Model, cancellationToken))
                yield return p;

            yield break;
        }

        var messages = chatRequest.Messages.ToPollinationMessages();
        var metadata = chatRequest.GetProviderMetadata<PollinationsProviderMetadata>(GetIdentifier());
        // ---------- 1) Build request payload ----------
        var payload = new
        {
            model = chatRequest.Model,
            stream = true,
            max_tokens = chatRequest.MaxOutputTokens,
            temperature = chatRequest.Temperature,
            reasoning_effort = metadata?.ReasoningEffort,
            /*      tools = chatRequest.Tools?.Select(a => new
                  {
                      type = "tool_type",
                      function = new
                      {
                          name = a.Name,
                          description = a.Description,
                          parameters = a.InputSchema
                      }
                  }) ?? [],*/
            messages,
        };

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "openai")
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
            yield return $"Pollinations API error: {err}".ToErrorUIPart();
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? id = null;
        bool started = false;
        // token counters
        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;

        // buffers/state used across chunks
        var toolBuffers = new Dictionary<string, (string? ToolName, StringBuilder Args, int Index)>();
        var indexToKey = new Dictionary<int, string>();   // map streaming "index" -> canonical key (id or synthetic)
        var toolStartSent = new HashSet<string>();

        string ResolveKey(string? idPart, int index)
        {
            if (!string.IsNullOrEmpty(idPart)) return idPart!;
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
            if (s is "\"" or "''") return false;
            return (s.StartsWith("{") && s.EndsWith("}")) || (s.StartsWith("[") && s.EndsWith("]"));
        }

        async IAsyncEnumerable<UIMessagePart> FlushToolCalls(string? reason = null)
        {
            // Emit valid tool calls in index order
            foreach (var kv in toolBuffers.OrderBy(kv => kv.Value.Index).ToArray())
            {
                var key = kv.Key;
                var (toolName, sb, _) = kv.Value;

                if (string.IsNullOrEmpty(toolName))
                    continue; // never emit without a known name

                var raw = sb.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw) || raw is "\"" or "''")
                    continue; // ignore empty

                object inputObj;
                try
                {
                    if (LooksLikeJson(raw))
                        inputObj = JsonSerializer.Deserialize<object>(raw)!;
                    else
                        inputObj = new { value = raw }; // graceful fallback if provider sends plain text
                }
                catch (JsonException)
                {
                    continue; // keep buffering if the stream will continue
                }

                yield return new ToolCallPart
                {
                    ToolCallId = key,
                    ToolName = toolName!,
                    ProviderExecuted = false,
                    Input = inputObj
                };

                toolBuffers.Remove(key);
                toolStartSent.Remove(key);

                // clear any index aliases pointing to this key
                var toRemove = indexToKey.Where(p => p.Value == key).Select(p => p.Key).ToList();
                foreach (var r in toRemove) indexToKey.Remove(r);
            }
        }

        string completionModel = chatRequest.Model;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break; // EOF

            if (line.Length == 0 || line.StartsWith(":")) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..].Trim();
            if (data is "[DONE]" or "[done]") break;

            JsonDocument doc = JsonDocument.Parse(data);
            //            try { doc = JsonDocument.Parse(data); }
            //          catch { continue; }

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

            if (root.TryGetProperty("model", out var reqModel) && reqModel.ValueKind == JsonValueKind.String)
            {
                completionModel = reqModel.GetString()!;
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

                            yield return new TextDeltaUIMessageStreamPart { Id = id!, Delta = text };
                        }
                    }

                    // ---- tool call streaming ----
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

                            // Resolve key and remember index alias if an id is provided
                            var key = ResolveKey(idPart, index);
                            if (!string.IsNullOrEmpty(idPart)) RememberAlias(key, index);

                            // Ensure buffer exists
                            if (!toolBuffers.TryGetValue(key, out var buf))
                                buf = (ToolName: null, Args: new StringBuilder(), Index: index);

                            // Capture name (may arrive only once)
                            if (fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            {
                                buf.ToolName = nameEl.GetString();
                                toolBuffers[key] = buf;

                                // Emit streaming start once, and only when name is known
                                if (!string.IsNullOrEmpty(buf.ToolName) && !toolStartSent.Contains(key))
                                {
                                    yield return new ToolCallStreamingStartPart
                                    {
                                        ToolCallId = key,
                                        ToolName = buf.ToolName!,
                                        ProviderExecuted = false
                                    };
                                    toolStartSent.Add(key);
                                }
                            }
                            else
                            {
                                // keep whatever we had
                                toolBuffers[key] = buf;
                            }

                            // Accumulate argument fragments (string-chunked JSON)
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

                    // ---- finalize on finish_reason: "tool_calls" OR "stop"
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        var reason = fr.GetString();
                        if (reason is "tool_calls" or "stop")
                        {
                            await foreach (var part in FlushToolCalls(reason))
                                yield return part;
                        }
                    }
                }
            }

            doc.Dispose();
        }

        // EOF safety: provider ended without an explicit finish_reason
        if (toolBuffers.Count > 0)
        {
            await foreach (var part in FlushToolCalls("eof"))
                yield return part;
        }

        if (id is not null)
            yield return id.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            completionModel,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature
        );
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
