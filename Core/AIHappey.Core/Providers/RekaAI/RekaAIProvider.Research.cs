using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Vercel.Extensions;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    private const string ResearchModelId = "reka-flash-research";

    private static bool IsResearchModel(string? model)
        => string.Equals(model, ResearchModelId, StringComparison.OrdinalIgnoreCase);

    private void ApplyResearchAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(RekaAI)} API key.");

        _client.DefaultRequestHeaders.Remove("X-Api-Key");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private async Task<ChatCompletion> CompleteRekaResearchChatAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var payload = BuildRekaResearchPayload(options, stream: false);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"RekaAI Research API error ({(int)resp.StatusCode}): {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ToRekaResearchChatCompletion(doc.RootElement, options.Model);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteRekaResearchChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildRekaResearchPayload(options, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"RekaAI Research API error ({(int)resp.StatusCode}): {err}");
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
            var update = ToRekaResearchChatCompletionUpdate(doc.RootElement, options.Model);
            if (update is not null)
                yield return update;
        }
    }

    private async IAsyncEnumerable<UIMessagePart> StreamRekaResearchAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildRekaResearchPayload(chatRequest, stream: true);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return $"RekaAI Research API error: {err}".ToErrorUIPart();
            yield break;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string streamId = Guid.NewGuid().ToString("n");
        bool textStarted = false;
        string finishReason = "stop";

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;

        var fullMessageText = new StringBuilder();

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

            ExtractOpenAiUsage(root, ref promptTokens, ref completionTokens, ref totalTokens);

            if (!root.TryGetProperty("choices", out var choicesEl)
                || choicesEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var choiceEl in choicesEl.EnumerateArray())
            {
                if (choiceEl.TryGetProperty("finish_reason", out var finishEl)
                    && finishEl.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(finishEl.GetString()))
                {
                    finishReason = finishEl.GetString()!;
                }

                if (!choiceEl.TryGetProperty("delta", out var deltaEl)
                    || deltaEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!deltaEl.TryGetProperty("content", out var contentEl)
                    || contentEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var delta = contentEl.GetString();
                if (string.IsNullOrWhiteSpace(delta))
                    continue;

                if (!textStarted)
                {
                    yield return streamId.ToTextStartUIMessageStreamPart();
                    textStarted = true;
                }

                fullMessageText.Append(delta);
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = streamId,
                    Delta = delta
                };
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

        yield return finishReason.ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

    private static Dictionary<string, object?> BuildRekaResearchPayload(ChatCompletionOptions options, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ResearchModelId,
            ["stream"] = stream,
            ["messages"] = ToRekaResearchMessages(options.Messages)
        };

        if (options.Temperature is not null)
            payload["temperature"] = options.Temperature;

        if (options.ResponseFormat is not null)
            payload["response_format"] = options.ResponseFormat;

        return payload;
    }

    private static Dictionary<string, object?> BuildRekaResearchPayload(ChatRequest chatRequest, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = ResearchModelId,
            ["stream"] = stream,
            ["messages"] = ToRekaResearchMessages(chatRequest.Messages),
            ["temperature"] = chatRequest.Temperature
        };

        if (chatRequest.MaxOutputTokens is not null)
            payload["max_tokens"] = chatRequest.MaxOutputTokens;

        if (chatRequest.ResponseFormat is not null)
            payload["response_format"] = chatRequest.ResponseFormat;

        return payload;
    }

    private static IEnumerable<object> ToRekaResearchMessages(IEnumerable<ChatMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return [];

        var mapped = new List<object>();
        var systemTexts = new List<string>();

        foreach (var msg in all)
        {
            var role = (msg.Role ?? string.Empty).Trim().ToLowerInvariant();

            if (role == "system")
            {
                var sys = ExtractTextFromCompletionContent(msg.Content);
                if (!string.IsNullOrWhiteSpace(sys))
                    systemTexts.Add(sys!);
                continue;
            }

            if (role is not ("user" or "assistant"))
                continue;

            var text = ExtractTextFromCompletionContent(msg.Content);
            if (string.IsNullOrWhiteSpace(text))
                text = msg.Content.GetRawText();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            mapped.Add(new
            {
                role,
                content = text
            });
        }

        return MergeSystemTextIntoResearchMessages(mapped, systemTexts);
    }

    private static IEnumerable<object> ToRekaResearchMessages(IEnumerable<UIMessage> messages)
    {
        var all = messages?.ToList() ?? [];
        if (all.Count == 0)
            return [];

        var mapped = new List<object>();
        var systemTexts = new List<string>();

        foreach (var msg in all)
        {
            var text = string.Join("\n", msg.Parts
                .OfType<TextUIPart>()
                .Select(a => a.Text)
                .Where(a => !string.IsNullOrWhiteSpace(a)));

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (msg.Role == Role.system)
            {
                systemTexts.Add(text);
                continue;
            }

            if (msg.Role is not (Role.user or Role.assistant))
                continue;

            mapped.Add(new
            {
                role = msg.Role == Role.user ? "user" : "assistant",
                content = text
            });
        }

        return MergeSystemTextIntoResearchMessages(mapped, systemTexts);
    }

    private static IEnumerable<object> MergeSystemTextIntoResearchMessages(List<object> mapped, List<string> systemTexts)
    {
        if (systemTexts.Count == 0)
            return mapped;

        var systemPrefix = $"System instruction:\n{string.Join("\n\n", systemTexts)}\n\n";
        var firstUserIdx = mapped.FindIndex(m =>
        {
            var el = JsonSerializer.SerializeToElement(m, JsonSerializerOptions.Web);
            return el.TryGetProperty("role", out var roleEl)
                && roleEl.ValueKind == JsonValueKind.String
                && string.Equals(roleEl.GetString(), "user", StringComparison.OrdinalIgnoreCase);
        });

        if (firstUserIdx >= 0)
        {
            var userEl = JsonSerializer.SerializeToElement(mapped[firstUserIdx], JsonSerializerOptions.Web);
            var content = userEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
                ? cEl.GetString()
                : string.Empty;

            mapped[firstUserIdx] = new
            {
                role = "user",
                content = systemPrefix + content
            };
        }
        else
        {
            mapped.Insert(0, new { role = "user", content = systemPrefix.TrimEnd() });
        }

        return mapped;
    }

    private static ChatCompletion ToRekaResearchChatCompletion(JsonElement root, string fallbackModel)
    {
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString() ?? fallbackModel
            : fallbackModel;

        var choices = new List<object>();
        if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var choiceEl in choicesEl.EnumerateArray())
            {
                if (choiceEl.ValueKind != JsonValueKind.Object)
                    continue;

                var index = choiceEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32()
                    : idx++;

                var finishReason = choiceEl.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String
                    ? frEl.GetString()
                    : "stop";

                string role = "assistant";
                string? content = null;
                object? reasoningSteps = null;
                string? reasoningContent = null;
                object? annotations = null;

                if (choiceEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
                {
                    if (msgEl.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                        role = roleEl.GetString() ?? "assistant";

                    if (msgEl.TryGetProperty("content", out var contentEl))
                    {
                        content = contentEl.ValueKind == JsonValueKind.String
                            ? contentEl.GetString()
                            : contentEl.GetRawText();
                    }

                    if (msgEl.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String)
                        reasoningContent = rcEl.GetString();

                    if (msgEl.TryGetProperty("reasoning_steps", out var rsEl) && rsEl.ValueKind == JsonValueKind.Array)
                        reasoningSteps = JsonSerializer.Deserialize<object>(rsEl.GetRawText(), JsonSerializerOptions.Web);

                    if (msgEl.TryGetProperty("annotations", out var annEl) && annEl.ValueKind == JsonValueKind.Array)
                        annotations = JsonSerializer.Deserialize<object>(annEl.GetRawText(), JsonSerializerOptions.Web);
                }

                var message = new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["content"] = content
                };

                if (reasoningContent is not null)
                    message["reasoning_content"] = reasoningContent;
                if (reasoningSteps is not null)
                    message["reasoning_steps"] = reasoningSteps;
                if (annotations is not null)
                    message["annotations"] = annotations;

                choices.Add(new
                {
                    index,
                    message,
                    finish_reason = finishReason
                });
            }
        }

        object usage = new
        {
            prompt_tokens = 0,
            completion_tokens = 0,
            total_tokens = 0
        };
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var prompt = usageEl.TryGetProperty("prompt_tokens", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                ? pEl.GetInt32()
                : 0;
            var completion = usageEl.TryGetProperty("completion_tokens", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                ? cEl.GetInt32()
                : 0;
            var total = usageEl.TryGetProperty("total_tokens", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? tEl.GetInt32()
                : prompt + completion;

            usage = new
            {
                prompt_tokens = prompt,
                completion_tokens = completion,
                total_tokens = total
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

    private static ChatCompletionUpdate? ToRekaResearchChatCompletionUpdate(JsonElement root, string fallbackModel)
    {
        var choices = new List<object>();
        if (root.TryGetProperty("choices", out var choicesEl) && choicesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var choiceEl in choicesEl.EnumerateArray())
            {
                if (choiceEl.ValueKind != JsonValueKind.Object)
                    continue;

                var index = choiceEl.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number
                    ? idxEl.GetInt32()
                    : 0;

                string? finishReason = null;
                if (choiceEl.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                    finishReason = frEl.GetString();

                var delta = new Dictionary<string, object?>();
                if (choiceEl.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.Object)
                {
                    if (deltaEl.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                        delta["role"] = roleEl.GetString();

                    if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                        delta["content"] = contentEl.GetString();

                    if (deltaEl.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String)
                        delta["reasoning_content"] = rcEl.GetString();

                    if (deltaEl.TryGetProperty("reasoning_steps", out var rsEl)
                        && rsEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                    {
                        delta["reasoning_steps"] = JsonSerializer.Deserialize<object>(rsEl.GetRawText(), JsonSerializerOptions.Web);
                    }
                }

                choices.Add(new
                {
                    index,
                    delta,
                    finish_reason = finishReason
                });
            }
        }

        object? usage = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var prompt = usageEl.TryGetProperty("prompt_tokens", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                ? pEl.GetInt32()
                : 0;
            var completion = usageEl.TryGetProperty("completion_tokens", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                ? cEl.GetInt32()
                : 0;
            var total = usageEl.TryGetProperty("total_tokens", out var tEl) && tEl.ValueKind == JsonValueKind.Number
                ? tEl.GetInt32()
                : prompt + completion;

            usage = new
            {
                prompt_tokens = prompt,
                completion_tokens = completion,
                total_tokens = total
            };
        }

        if (choices.Count == 0 && usage is null)
            return null;

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

    private static void ExtractOpenAiUsage(JsonElement root, ref int promptTokens, ref int completionTokens, ref int totalTokens)
    {
        if (!root.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("prompt_tokens", out var promptEl) && promptEl.ValueKind == JsonValueKind.Number)
            promptTokens = promptEl.GetInt32();

        if (usage.TryGetProperty("completion_tokens", out var completionEl) && completionEl.ValueKind == JsonValueKind.Number)
            completionTokens = completionEl.GetInt32();

        if (usage.TryGetProperty("total_tokens", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number)
            totalTokens = totalEl.GetInt32();
        else
            totalTokens = promptTokens + completionTokens;
    }
}
