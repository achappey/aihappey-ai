using AIHappey.Common.Model.ChatCompletions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    private async Task<ChatCompletion> CompleteRekaChatAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var payload = BuildRekaPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"RekaAI API error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ToChatCompletion(doc.RootElement, options.Model);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteRekaChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildRekaPayload(options, stream: true);
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
            throw new HttpRequestException($"RekaAI API error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.Length == 0 || line.StartsWith(":"))
                continue;

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0)
                continue;

            if (data is "[DONE]" or "[done]")
                yield break;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            var update = ToChatCompletionUpdate(root, options.Model);
            if (update is not null)
                yield return update;
        }
    }


    private static Dictionary<string, object?> BuildRekaPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["stream"] = stream,
            ["temperature"] = options.Temperature,
            ["messages"] = ToRekaMessages(options.Messages)
        };

        if (options.ToolChoice is not null)
            payload["tool_choice"] = options.ToolChoice;

        var tools = ToRekaTools(options.Tools).ToArray();
        if (tools.Length > 0)
            payload["tools"] = tools;

        return payload;
    }

    private static IEnumerable<object> ToRekaMessages(IEnumerable<ChatMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return [];

        var mapped = new List<object>();
        var systemTexts = new List<string>();
        var pendingSystemPrefix = (string?)null;

        foreach (var msg in all)
        {
            var role = (msg.Role ?? string.Empty).Trim().ToLowerInvariant();
            switch (role)
            {
                case "system":
                    {
                        var text = ExtractTextFromCompletionContent(msg.Content);
                        if (!string.IsNullOrWhiteSpace(text))
                            systemTexts.Add(text!);
                        break;
                    }

                case "user":
                case "assistant":
                    {
                        var content = ParseContentElement(msg.Content);

                        if (role == "user" && !string.IsNullOrEmpty(pendingSystemPrefix) && content is not null)
                        {
                            if (content is string s)
                            {
                                content = pendingSystemPrefix + s;
                                pendingSystemPrefix = null;
                            }
                            else
                            {
                                var list = new List<object> { new { type = "text", text = pendingSystemPrefix.TrimEnd() } };

                                if (content is IEnumerable<object> arr)
                                    list.AddRange(arr);
                                else
                                    list.Add(content);

                                content = list;
                                pendingSystemPrefix = null;
                            }
                        }

                        mapped.Add(new { role, content, tool_calls = ToRekaToolCalls(msg.ToolCalls) });
                        break;
                    }

                case "tool":
                    {
                        var output = ExtractTextFromCompletionContent(msg.Content) ?? msg.Content.GetRawText();
                        if (string.IsNullOrWhiteSpace(output))
                            output = "{}";

                        mapped.Add(new
                        {
                            role = "tool_output",
                            content = new[]
                            {
                                new
                                {
                                    tool_call_id = msg.ToolCallId ?? string.Empty,
                                    output
                                }
                            }
                        });
                        break;
                    }
            }
        }

        if (systemTexts.Count > 0)
        {
            pendingSystemPrefix = $"System instruction:\n{string.Join("\n\n", systemTexts)}\n\n";
        }

        if (!string.IsNullOrEmpty(pendingSystemPrefix))
        {
            var firstUser = mapped.FindIndex(m =>
            {
                var el = JsonSerializer.SerializeToElement(m, JsonSerializerOptions.Web);
                return el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("role", out var roleEl)
                    && roleEl.ValueKind == JsonValueKind.String
                    && string.Equals(roleEl.GetString(), "user", StringComparison.OrdinalIgnoreCase);
            });

            if (firstUser >= 0)
            {
                var userEl = JsonSerializer.SerializeToElement(mapped[firstUser], JsonSerializerOptions.Web);
                if (userEl.TryGetProperty("content", out var contentEl))
                {
                    object mergedContent;
                    if (contentEl.ValueKind == JsonValueKind.String)
                    {
                        mergedContent = pendingSystemPrefix + contentEl.GetString();
                    }
                    else if (contentEl.ValueKind == JsonValueKind.Array)
                    {
                        var items = new List<object> { new { type = "text", text = pendingSystemPrefix.TrimEnd() } };
                        foreach (var it in contentEl.EnumerateArray())
                        {
                            var obj = JsonSerializer.Deserialize<object>(it.GetRawText(), JsonSerializerOptions.Web);
                            if (obj is not null)
                                items.Add(obj);
                        }

                        mergedContent = items;
                    }
                    else
                    {
                        mergedContent = pendingSystemPrefix.TrimEnd();
                    }

                    object? toolCalls = userEl.TryGetProperty("tool_calls", out var tcEl)
                        ? JsonSerializer.Deserialize<object>(tcEl.GetRawText(), JsonSerializerOptions.Web)
                        : null;

                    mapped[firstUser] = new
                    {
                        role = "user",
                        content = mergedContent,
                        tool_calls = toolCalls
                    };
                }
            }
            else
            {
                mapped.Insert(0, new
                {
                    role = "user",
                    content = pendingSystemPrefix.TrimEnd()
                });
            }
        }

        return mapped;
    }


    private static ChatCompletion ToChatCompletion(JsonElement root, string fallbackModel)
    {
        string id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        string model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? fallbackModel
            : fallbackModel;

        var choices = new List<object>();

        if (root.TryGetProperty("responses", out var responses)
            && responses.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var response in responses.EnumerateArray())
            {
                var messageEl = response.TryGetProperty("message", out var msgEl)
                    ? msgEl
                    : response.TryGetProperty("chunk", out var chunkEl)
                        ? chunkEl
                        : default;

                if (messageEl.ValueKind != JsonValueKind.Object)
                    continue;

                var role = messageEl.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                    ? roleEl.GetString() ?? "assistant"
                    : "assistant";

                var contentText = messageEl.TryGetProperty("content", out var contentEl)
                    ? ExtractRekaText(contentEl)
                    : null;

                object? toolCalls = null;
                if (messageEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                {
                    var mapped = tcEl.EnumerateArray()
                        .Select(ToOpenAIToolCall)
                        .Where(x => x is not null)
                        .ToArray();

                    if (mapped.Length > 0)
                        toolCalls = mapped!;
                }

                var finishReason = response.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String
                    ? frEl.GetString()
                    : "stop";

                choices.Add(new
                {
                    index,
                    message = new
                    {
                        role,
                        content = contentText,
                        tool_calls = toolCalls
                    },
                    finish_reason = finishReason
                });

                index++;
            }
        }

        object? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var input = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number
                ? inEl.GetInt32()
                : 0;
            var output = usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number
                ? outEl.GetInt32()
                : 0;
            usage = new
            {
                prompt_tokens = input,
                completion_tokens = output,
                total_tokens = input + output
            };
        }

        return new ChatCompletion
        {
            Id = id,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices = choices,
            Usage = usage
        };
    }

    private static ChatCompletionUpdate? ToChatCompletionUpdate(JsonElement root, string fallbackModel)
    {
        if (!root.TryGetProperty("responses", out var responses)
            || responses.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var choices = new List<object>();
        int index = 0;

        foreach (var response in responses.EnumerateArray())
        {
            var chunk = response.TryGetProperty("chunk", out var chunkEl)
                ? chunkEl
                : response.TryGetProperty("message", out var msgEl)
                    ? msgEl
                    : default;

            if (chunk.ValueKind != JsonValueKind.Object)
                continue;

            var role = chunk.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String
                ? roleEl.GetString()
                : "assistant";

            var content = chunk.TryGetProperty("content", out var contentEl)
                ? ExtractRekaText(contentEl)
                : null;

            object? toolCalls = null;
            if (chunk.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
            {
                var mapped = tcEl.EnumerateArray()
                    .Select(ToOpenAIToolCall)
                    .Where(x => x is not null)
                    .ToArray();
                if (mapped.Length > 0)
                    toolCalls = mapped;
            }

            var delta = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(role))
                delta["role"] = role;
            if (!string.IsNullOrWhiteSpace(content))
                delta["content"] = content;
            if (toolCalls is not null)
                delta["tool_calls"] = toolCalls;

            var finishReason = response.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String
                ? frEl.GetString()
                : null;

            choices.Add(new
            {
                index,
                delta,
                finish_reason = finishReason
            });
            index++;
        }

        if (choices.Count == 0)
            return null;

        object? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var input = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.ValueKind == JsonValueKind.Number
                ? inEl.GetInt32()
                : 0;
            var output = usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.ValueKind == JsonValueKind.Number
                ? outEl.GetInt32()
                : 0;
            usage = new
            {
                prompt_tokens = input,
                completion_tokens = output,
                total_tokens = input + output
            };
        }

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? fallbackModel
            : fallbackModel;

        return new ChatCompletionUpdate
        {
            Id = id,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model,
            Choices = choices,
            Usage = usage
        };
    }
}
