using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private static readonly JsonSerializerOptions InworldJsonOptions = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ChatCompletion> ChatCompletionsCompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        if (options.Stream == true)
            throw new ArgumentException("Use CompleteChatStreamingAsync for stream=true.", nameof(options));

        var payload = BuildInworldChatPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, InworldJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "llm/v1alpha/completions:completeChat")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Inworld error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var resultElement = GetResultElement(doc.RootElement);

        return MapInworldChatCompletion(resultElement, options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> ChatCompletionsCompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var payload = BuildInworldChatPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, InworldJsonOptions);

        using var req = new HttpRequestMessage(HttpMethod.Post, "llm/v1alpha/completions:completeChat")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Inworld stream error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) yield break;
            if (line.Length == 0) continue;

            ChatCompletionUpdate? update = null;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var resultElement = GetResultElement(doc.RootElement);
                update = MapInworldChatCompletionUpdate(resultElement, options.Model);
            }
            catch (JsonException)
            {
                continue;
            }

            if (update is not null)
                yield return update;
        }
    }

    private object BuildInworldChatPayload(ChatCompletionOptions options, bool stream)
    {
        var messages = ToInworldMessages(options.Messages).ToList();
        var userId = ResolveEndUserId(options.Model);
        var (Provider, Model) = options.Model.SplitModelId();
        var payload = new Dictionary<string, object?>
        {
            ["servingId"] = new
            {
                modelId = new
                {
                    model = Model,
                    serviceProvider = Provider
                },
                userId
            },
            ["messages"] = messages,
            ["textGenerationConfig"] = new Dictionary<string, object?>
            {
                ["temperature"] = options.Temperature,
                ["stream"] = stream
            },
            ["tools"] = options.Tools?.Any() == true ? options.Tools : null,
            ["toolChoice"] = options.ToolChoice,
            ["responseFormat"] = options.ResponseFormat
        };

        return payload;
    }

    private object BuildInworldChatPayload(ChatRequest chatRequest, bool stream)
    {
        var messages = ToInworldMessages(chatRequest.Messages).ToList();
        var userId = _endUserIdResolver.Resolve(chatRequest) ?? "anonymous";
        var (Provider, Model) = chatRequest.Model.SplitModelId();

        var payload = new Dictionary<string, object?>
        {
            ["servingId"] = new
            {
                modelId = new
                {
                    model = Model,
                    serviceProvider = Provider
                },
                userId
            },
            ["messages"] = messages,
            ["textGenerationConfig"] = new Dictionary<string, object?>
            {
                ["temperature"] = chatRequest.Temperature,
                ["maxTokens"] = chatRequest.MaxOutputTokens,
                ["topP"] = chatRequest.TopP,
                ["stream"] = stream
            },
            ["tools"] = chatRequest.Tools?.Count > 0 ? chatRequest.Tools : null,
            ["toolChoice"] = chatRequest.ToolChoice,
            ["responseFormat"] = chatRequest.ResponseFormat
        };

        return payload;
    }

    private static ChatCompletion MapInworldChatCompletion(JsonElement resultElement, string fallbackModel)
    {
        var id = resultElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = resultElement.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = TryGetUnixTimestamp(resultElement, "createTime");

        object? usage = null;
        if (resultElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            usage = MapUsage(usageEl);

        var choices = new List<object>();
        if (resultElement.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var choiceEl in chEl.EnumerateArray())
            {
                choices.Add(MapChoice(choiceEl, index));
                index++;
            }
        }

        return new ChatCompletion
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Created = created,
            Model = model ?? fallbackModel,
            Choices = choices,
            Usage = usage
        };
    }

    private static ChatCompletionUpdate MapInworldChatCompletionUpdate(JsonElement resultElement, string fallbackModel)
    {
        var id = resultElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var model = resultElement.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
        var created = TryGetUnixTimestamp(resultElement, "createTime");

        object? usage = null;
        if (resultElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            usage = MapUsage(usageEl);

        var choices = new List<object>();
        if (resultElement.TryGetProperty("choices", out var chEl) && chEl.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var choiceEl in chEl.EnumerateArray())
            {
                choices.Add(MapChoiceDelta(choiceEl, index));
                index++;
            }
        }

        return new ChatCompletionUpdate
        {
            Id = id ?? Guid.NewGuid().ToString("n"),
            Created = created,
            Model = model ?? fallbackModel,
            Choices = choices,
            Usage = usage
        };
    }

    private static object MapChoice(JsonElement choiceEl, int index)
    {
        var finishReason = choiceEl.TryGetProperty("finishReason", out var frEl)
            ? MapFinishReason(frEl.GetString())
            : null;

        object? message = null;
        if (choiceEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
        {
            var role = MapRoleToOpenAi(msgEl.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null);
            var content = msgEl.TryGetProperty("content", out var cEl)
                ? (cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : cEl.GetRawText())
                : null;

            object? toolCalls = null;
            if (msgEl.TryGetProperty("toolCalls", out var tcEl)
                && tcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                toolCalls = JsonSerializer.Deserialize<object>(tcEl.GetRawText(), InworldJsonOptions);
            }

            var reasoning = msgEl.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String
                ? rEl.GetString()
                : null;

            message = new
            {
                role,
                content,
                reasoning,
                tool_calls = toolCalls
            };
        }

        return new
        {
            index,
            finish_reason = finishReason,
            message
        };
    }

    private static object MapChoiceDelta(JsonElement choiceEl, int index)
    {
        var finishReason = choiceEl.TryGetProperty("finishReason", out var frEl)
            ? MapFinishReason(frEl.GetString())
            : null;

        var delta = new Dictionary<string, object?>();
        if (choiceEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
        {
            var role = MapRoleToOpenAi(msgEl.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : null);
            if (!string.IsNullOrWhiteSpace(role))
                delta["role"] = role;

            if (msgEl.TryGetProperty("content", out var cEl))
            {
                var content = cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : cEl.GetRawText();
                if (!string.IsNullOrWhiteSpace(content))
                    delta["content"] = content;
            }

            if (msgEl.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                delta["reasoning"] = rEl.GetString();

            if (msgEl.TryGetProperty("toolCalls", out var tcEl)
                && tcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                delta["tool_calls"] = JsonSerializer.Deserialize<object>(tcEl.GetRawText(), InworldJsonOptions);
            }
        }

        return new
        {
            index,
            delta,
            finish_reason = finishReason
        };
    }

    private static long TryGetUnixTimestamp(JsonElement resultElement, string propertyName)
    {
        if (resultElement.TryGetProperty(propertyName, out var timeEl)
            && timeEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timeEl.GetString(), out var dto))
        {
            return dto.ToUnixTimeSeconds();
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static object MapUsage(JsonElement usageEl)
    {
        var completionTokens = usageEl.TryGetProperty("completionTokens", out var ct) && ct.ValueKind == JsonValueKind.Number
            ? ct.GetInt32()
            : 0;

        var promptTokens = usageEl.TryGetProperty("promptTokens", out var pt) && pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt32()
            : 0;

        var reasoningTokens = usageEl.TryGetProperty("reasoningTokens", out var rt) && rt.ValueKind == JsonValueKind.Number
            ? rt.GetInt32()
            : (int?)null;

        var estimatedCompletionTokens = usageEl.TryGetProperty("estimatedCompletionTokens", out var ect) && ect.ValueKind == JsonValueKind.Number
            ? ect.GetInt32()
            : (int?)null;

        var estimatedPromptTokens = usageEl.TryGetProperty("estimatedPromptTokens", out var ept) && ept.ValueKind == JsonValueKind.Number
            ? ept.GetInt32()
            : (int?)null;

        return new
        {
            completion_tokens = completionTokens,
            prompt_tokens = promptTokens,
            total_tokens = completionTokens + promptTokens,
            reasoning_tokens = reasoningTokens,
            estimated_completion_tokens = estimatedCompletionTokens,
            estimated_prompt_tokens = estimatedPromptTokens
        };
    }

    private static JsonElement GetResultElement(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
        {
            var message = errorEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : errorEl.GetRawText();
            throw new InvalidOperationException($"Inworld error: {message}");
        }

        return root.TryGetProperty("result", out var resultEl) ? resultEl : root;
    }

    private static string MapFinishReason(string? inworldReason)
    {
        return inworldReason switch
        {
            "FINISH_REASON_STOP" => "stop",
            "FINISH_REASON_LENGTH" => "length",
            "FINISH_REASON_CONTENT_FILTER" => "content_filter",
            "FINISH_REASON_TOOL_CALL" => "tool_calls",
            _ => "stop"
        };
    }

    private static string? MapRoleToOpenAi(string? inworldRole)
    {
        return inworldRole switch
        {
            "MESSAGE_ROLE_SYSTEM" => "system",
            "MESSAGE_ROLE_USER" => "user",
            "MESSAGE_ROLE_ASSISTANT" => "assistant",
            "MESSAGE_ROLE_TOOL" => "tool",
            _ => null
        };
    }

    private static IEnumerable<object> ToInworldMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = MapRoleToInworld(msg.Role);
            if (string.IsNullOrWhiteSpace(role))
                continue;

            var mapped = new Dictionary<string, object?>
            {
                ["role"] = role
            };

            if (msg.ToolCalls is not null)
                mapped["toolCalls"] = msg.ToolCalls;

            if (!string.IsNullOrWhiteSpace(msg.ToolCallId))
                mapped["toolCallId"] = msg.ToolCallId;

            var contents = MapInworldContentItems(msg.Content).ToList();
            if (contents.Count == 1 && contents[0] is string contentText)
            {
                mapped["content"] = contentText;
            }
            else if (contents.Count > 0)
            {
                mapped["contents"] = contents;
            }

            if (mapped.ContainsKey("content") || mapped.ContainsKey("contents"))
                yield return mapped;
        }
    }

    private static IEnumerable<object> ToInworldMessages(IEnumerable<UIMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = MapRoleToInworld(msg.Role switch
            {
                Role.system => "system",
                Role.user => "user",
                Role.assistant => "assistant",
                _ => ""
            });

            if (string.IsNullOrWhiteSpace(role))
                continue;

            var contents = new List<object>();
            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                        contents.Add(new { text = text.Text });
                        break;
                    case FileUIPart file when file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase):
                        contents.Add(new { imageUrl = new { url = file.Url } });
                        break;
                }
            }

            if (contents.Count == 0)
                continue;

            var mapped = new Dictionary<string, object?>
            {
                ["role"] = role
            };

            var contentText = TryGetSingleText(contents);
            if (!string.IsNullOrWhiteSpace(contentText))
                mapped["content"] = contentText;
            else
                mapped["contents"] = contents;

            yield return mapped;
        }
    }

    private static string? TryGetSingleText(IReadOnlyList<object> contents)
    {
        if (contents.Count != 1)
            return null;

        var element = JsonSerializer.SerializeToElement(contents[0], InworldJsonOptions);
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("text", out var textEl)
            && textEl.ValueKind == JsonValueKind.String)
        {
            return textEl.GetString();
        }

        return null;
    }

    private static IEnumerable<object> MapInworldContentItems(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                yield return text;
            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            if (content.ValueKind != JsonValueKind.Null)
                yield return content.GetRawText();
            yield break;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var type = item.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            switch (type)
            {
                case "text":
                    if (item.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                        yield return new { text = textEl.GetString() };
                    break;
                case "image_url":
                    if (item.TryGetProperty("image_url", out var imgEl) && imgEl.ValueKind == JsonValueKind.Object)
                    {
                        var url = imgEl.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                        var detail = imgEl.TryGetProperty("detail", out var detailEl) ? detailEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            yield return new
                            {
                                imageUrl = new
                                {
                                    url,
                                    detail
                                }
                            };
                        }
                    }
                    break;
            }
        }
    }

    private string ResolveEndUserId(string modelId)
    {
        var fallbackRequest = new ChatRequest
        {
            Id = Guid.NewGuid().ToString("n"),
            Model = modelId,
            Messages = []
        };

        return _endUserIdResolver.Resolve(fallbackRequest) ?? "anonymous";
    }

    private static string? MapRoleToInworld(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "system" => "MESSAGE_ROLE_SYSTEM",
            "user" => "MESSAGE_ROLE_USER",
            "assistant" => "MESSAGE_ROLE_ASSISTANT",
            "tool" => "MESSAGE_ROLE_TOOL",
            _ => null
        };
    }
}
