using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsResearchModel(chatRequest.GetModelId()))
        {
            ApplyResearchAuthHeader();

            await foreach (var part in StreamRekaResearchAsync(chatRequest, cancellationToken))
                yield return part;

            yield break;
        }

        ApplyAuthHeader();

        var payload = BuildRekaPayload(chatRequest, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(
            req,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"RekaAI API error: {err}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string streamId = Guid.NewGuid().ToString("n");
        bool textStarted = false;

        int inputTokens = 0;
        int outputTokens = 0;
        int totalTokens = 0;
        int emittedLength = 0;

        var emittedToolCalls = new HashSet<string>(StringComparer.Ordinal);
        StringBuilder fullMessageText = new();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0 || line.StartsWith(":"))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data is "[DONE]" or "[done]")
                break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            ExtractUsage(root, ref inputTokens, ref outputTokens, ref totalTokens);

            if (!root.TryGetProperty("responses", out var responses)
                || responses.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var response in responses.EnumerateArray())
            {
                var chunk = response.TryGetProperty("chunk", out var chunkEl) && chunkEl.ValueKind == JsonValueKind.Object
                    ? chunkEl
                    : response.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object
                        ? msgEl
                        : default;

                if (chunk.ValueKind == JsonValueKind.Object)
                {
                    var text = chunk.TryGetProperty("content", out var contentEl)
                        ? ExtractRekaText(contentEl)
                        : null;

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        fullMessageText.Clear();
                        fullMessageText.Append(text);

                        if (fullMessageText.Length > emittedLength)
                        {
                            var delta = fullMessageText
                                .ToString(emittedLength, fullMessageText.Length - emittedLength);

                            emittedLength = fullMessageText.Length;

                            yield return new TextDeltaUIMessageStreamPart
                            {
                                Id = streamId,
                                Delta = delta
                            };
                        }

                    }

                    if (chunk.TryGetProperty("tool_calls", out var toolCallsEl)
                        && toolCallsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in toolCallsEl.EnumerateArray())
                        {
                            if (tc.ValueKind != JsonValueKind.Object)
                                continue;

                            if (!TryMapRekaToolCall(tc, out var callId, out var toolName, out var input))
                                continue;

                            if (!emittedToolCalls.Add(callId))
                                continue;

                            yield return new ToolCallStreamingStartPart
                            {
                                ToolCallId = callId,
                                ToolName = toolName,
                                Title = chatRequest.Tools?.FirstOrDefault(t => t.Name == toolName)?.Title,
                                ProviderExecuted = false
                            };

                            yield return new ToolCallPart
                            {
                                ToolCallId = callId,
                                ToolName = toolName,
                                Title = chatRequest.Tools?.FirstOrDefault(t => t.Name == toolName)?.Title,
                                ProviderExecuted = false,
                                Input = input
                            };

                            yield return new ToolApprovalRequestPart
                            {
                                ToolCallId = callId,
                                ApprovalId = Guid.NewGuid().ToString()
                            };
                        }
                    }
                }
            }
        }

        if (textStarted)
            yield return streamId.ToTextEndUIMessageStreamPart();

        if (chatRequest.ResponseFormat is not null && fullMessageText.Length > 0)
        {
            DataUIPart? dataPart = null;
            try
            {
                var schema = chatRequest.ResponseFormat.GetJSONSchema();
                var dataObject = JsonSerializer.Deserialize<object>(fullMessageText.ToString(), JsonSerializerOptions.Web);
                if (dataObject is not null)
                {
                    dataPart = new DataUIPart
                    {
                        Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                        Data = dataObject
                    };
                }
            }
            catch
            {
                // Ignore schema parse issues for provider-specific malformed output.
            }

            if (dataPart is not null)
                yield return dataPart;
        }

        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: outputTokens,
            inputTokens: inputTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

    private static Dictionary<string, object?> BuildRekaPayload(ChatRequest chatRequest, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = chatRequest.Model,
            ["stream"] = stream,
            ["temperature"] = chatRequest.Temperature,
            ["messages"] = ToRekaMessages(chatRequest.Messages)
        };

        if (chatRequest.MaxOutputTokens is not null)
            payload["max_tokens"] = chatRequest.MaxOutputTokens;

        if (chatRequest.TopP is not null)
            payload["top_p"] = chatRequest.TopP;

        if (chatRequest.ToolChoice is not null)
            payload["tool_choice"] = chatRequest.ToolChoice;

        var tools = chatRequest.Tools?.Select(t => (object)new
        {
            name = t.Name,
            description = t.Description,
            parameters = t.InputSchema
        }).ToArray();

        if (tools?.Length > 0)
            payload["tools"] = tools;

        return payload;
    }


    private static IEnumerable<object> ToRekaTools(IEnumerable<object>? tools)
    {
        if (tools is null)
            yield break;

        foreach (var tool in tools)
        {
            if (tool is null)
                continue;

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tool, JsonSerializerOptions.Web));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                continue;

            var functionEl = root.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object
                ? f
                : root;

            if (!functionEl.TryGetProperty("name", out var nameEl)
                || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
            {
                continue;
            }

            object parameters = new { };
            if (functionEl.TryGetProperty("parameters", out var paramsEl)
                && paramsEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                parameters = JsonSerializer.Deserialize<object>(paramsEl.GetRawText(), JsonSerializerOptions.Web) ?? new { };
            }

            var description = functionEl.TryGetProperty("description", out var descEl)
                && descEl.ValueKind == JsonValueKind.String
                    ? descEl.GetString()
                    : null;

            yield return new
            {
                name = nameEl.GetString()!,
                description,
                parameters
            };
        }
    }

    private static IEnumerable<object> ToRekaMessages(IEnumerable<UIMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return [];

        var systemText = string.Join("\n\n", all
            .Where(m => m.Role == Role.system)
            .SelectMany(m => m.Parts.OfType<TextUIPart>())
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        var mapped = new List<object>();
        var pendingSystemPrefix = !string.IsNullOrWhiteSpace(systemText)
            ? $"System instruction:\n{systemText}\n\n"
            : null;

        foreach (var msg in all.Where(m => m.Role != Role.system))
        {
            switch (msg.Role)
            {
                case Role.user:
                    {
                        var parts = msg.Parts
                            .Select(MapPartToRekaInputContent)
                            .Where(p => p is not null)
                            .Select(p => p!)
                            .ToList();

                        if (!string.IsNullOrEmpty(pendingSystemPrefix))
                        {
                            var merged = false;
                            for (var i = 0; i < parts.Count; i++)
                            {
                                var partEl = JsonSerializer.SerializeToElement(parts[i], JsonSerializerOptions.Web);
                                if (partEl.ValueKind != JsonValueKind.Object)
                                    continue;

                                if (partEl.TryGetProperty("type", out var typeEl)
                                    && typeEl.ValueKind == JsonValueKind.String
                                    && string.Equals(typeEl.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                                    && partEl.TryGetProperty("text", out var textEl)
                                    && textEl.ValueKind == JsonValueKind.String)
                                {
                                    parts[i] = new { type = "text", text = pendingSystemPrefix + textEl.GetString() };
                                    merged = true;
                                    break;
                                }
                            }

                            if (!merged)
                                parts.Insert(0, new { type = "text", text = pendingSystemPrefix.TrimEnd() });

                            pendingSystemPrefix = null;
                        }

                        if (parts.Count == 1 && parts[0] is string single)
                            mapped.Add(new { role = "user", content = single });
                        else if (parts.Count > 0)
                            mapped.Add(new { role = "user", content = parts });

                        break;
                    }

                case Role.assistant:
                    {
                        var contentParts = new List<object>();
                        var toolCalls = new List<object>();
                        var toolOutputs = new List<object>();

                        foreach (var part in msg.Parts)
                        {
                            switch (part)
                            {
                                case TextUIPart t when !string.IsNullOrWhiteSpace(t.Text):
                                    contentParts.Add(new { type = "text", text = t.Text });
                                    break;

                                case ToolInvocationPart tip:
                                    {
                                        var toolName = tip.GetToolName();
                                        object parameters = tip.Input ?? new { };

                                        toolCalls.Add(new
                                        {
                                            id = tip.ToolCallId,
                                            name = toolName,
                                            parameters
                                        });

                                        if (tip.Output is not null)
                                        {
                                            var toolOutput = tip.Output is string s
                                                ? s
                                                : JsonSerializer.Serialize(tip.Output, JsonSerializerOptions.Web);

                                            toolOutputs.Add(new
                                            {
                                                tool_call_id = tip.ToolCallId,
                                                output = toolOutput
                                            });
                                        }

                                        break;
                                    }
                            }
                        }

                        if (contentParts.Count > 0 || toolCalls.Count > 0)
                        {
                            mapped.Add(new
                            {
                                role = "assistant",
                                content = contentParts.Count > 0 ? contentParts.ToArray() : null,
                                tool_calls = toolCalls.Count > 0 ? toolCalls.ToArray() : null
                            });
                        }

                        if (toolOutputs.Count > 0)
                        {
                            mapped.Add(new
                            {
                                role = "tool_output",
                                content = toolOutputs.ToArray()
                            });
                        }

                        break;
                    }
            }
        }

        if (!string.IsNullOrEmpty(pendingSystemPrefix))
        {
            mapped.Insert(0, new
            {
                role = "user",
                content = pendingSystemPrefix.TrimEnd()
            });
        }

        return mapped;
    }



    private static object? ToRekaToolCalls(IEnumerable<object>? toolCalls)
    {
        if (toolCalls is null)
            return null;

        var result = new List<object>();
        foreach (var tc in toolCalls)
        {
            if (tc is null)
                continue;

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(tc, JsonSerializerOptions.Web));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                continue;

            var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : Guid.NewGuid().ToString("n");

            var fn = root.TryGetProperty("function", out var fnEl) && fnEl.ValueKind == JsonValueKind.Object
                ? fnEl
                : root;

            var name = fn.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name))
                continue;

            object parameters = new { };
            if (fn.TryGetProperty("arguments", out var argsEl))
            {
                if (argsEl.ValueKind == JsonValueKind.String)
                {
                    var raw = argsEl.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        try
                        {
                            parameters = JsonSerializer.Deserialize<object>(raw!, JsonSerializerOptions.Web) ?? new { };
                        }
                        catch
                        {
                            parameters = new { value = raw };
                        }
                    }
                }
                else if (argsEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    parameters = JsonSerializer.Deserialize<object>(argsEl.GetRawText(), JsonSerializerOptions.Web) ?? new { };
                }
            }

            result.Add(new
            {
                id = id!,
                name = name!,
                parameters
            });
        }

        return result.Count > 0 ? result : null;
    }

    private static object? MapPartToRekaInputContent(UIMessagePart part)
    {
        return part switch
        {
            TextUIPart t when !string.IsNullOrWhiteSpace(t.Text) => new { type = "text", text = t.Text },
            FileUIPart f when !string.IsNullOrWhiteSpace(f.Url)
                && !string.IsNullOrWhiteSpace(f.MediaType)
                && f.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) =>
                new { type = "image_url", image_url = f.Url },
            _ => null
        };
    }


    private static bool TryMapRekaToolCall(JsonElement tc,
        out string callId,
        out string toolName,
        out object input)
    {
        callId = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        toolName = tc.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(toolName))
        {
            input = new { };
            return false;
        }

        if (tc.TryGetProperty("parameters", out var paramsEl)
            && paramsEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            input = JsonSerializer.Deserialize<object>(paramsEl.GetRawText(), JsonSerializerOptions.Web) ?? new { };
            return true;
        }

        input = new { };
        return true;
    }

    private static object? ToOpenAIToolCall(JsonElement tc)
    {
        if (tc.ValueKind != JsonValueKind.Object)
            return null;

        if (!tc.TryGetProperty("name", out var nameEl)
            || nameEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameEl.GetString()))
        {
            return null;
        }

        var id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        string arguments = "{}";
        if (tc.TryGetProperty("parameters", out var paramsEl)
            && paramsEl.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            arguments = paramsEl.GetRawText();
        }

        return new
        {
            id,
            type = "function",
            function = new
            {
                name = nameEl.GetString()!,
                arguments
            }
        };
    }

    private static string? ExtractRekaText(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join(string.Empty,
                content.EnumerateArray()
                    .Where(i => i.ValueKind == JsonValueKind.Object)
                    .Select(i =>
                        i.TryGetProperty("type", out var tEl)
                        && tEl.ValueKind == JsonValueKind.String
                        && string.Equals(tEl.GetString(), "text", StringComparison.OrdinalIgnoreCase)
                        && i.TryGetProperty("text", out var textEl)
                        && textEl.ValueKind == JsonValueKind.String
                            ? textEl.GetString()
                            : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => null
        };
    }

    private static void ExtractUsage(JsonElement root, ref int inputTokens, ref int outputTokens, ref int totalTokens)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("input_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number)
            inputTokens = inEl.GetInt32();
        if (usage.TryGetProperty("output_tokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number)
            outputTokens = outEl.GetInt32();

        totalTokens = inputTokens + outputTokens;
    }

    private static object? ParseContentElement(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        return JsonSerializer.Deserialize<object>(content.GetRawText(), JsonSerializerOptions.Web);
    }

    private static string? ExtractTextFromCompletionContent(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.Object)
                .Select(p => p.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString()
                    : null)
                .Where(t => !string.IsNullOrWhiteSpace(t))),
            _ => null
        };
    }
}
