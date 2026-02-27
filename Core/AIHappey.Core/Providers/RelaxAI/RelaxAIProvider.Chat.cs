using AIHappey.Core.AI;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.RelaxAI;

public partial class RelaxAIProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsDeepResearchModel(options.Model))
        {
            var payload = BuildDeepResearchPayload(options);
            return await _client.GetChatCompletion(
                 JsonSerializer.SerializeToElement(payload),
                 relativeUrl: "v1/deep-research",
                 ct: cancellationToken);
        }

        return await _client.GetChatCompletion(options, ct: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!IsDeepResearchModel(options.Model))
        {
            await foreach (var update in _client.GetChatCompletionUpdates(options, ct: cancellationToken))
                yield return update;

            yield break;
        }

        // RelaxAI deep-research endpoint does not support SSE.
        // Simulate chat-completions streaming via synthetic chunks.
        var result = await CompleteChatAsync(options, cancellationToken);

        var id = string.IsNullOrWhiteSpace(result.Id) ? Guid.NewGuid().ToString("n") : result.Id;
        var created = result.Created != 0 ? result.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = string.IsNullOrWhiteSpace(result.Model) ? options.Model : result.Model;

        string? content = null;
        object? toolCalls = null;
        string? finishReason = "stop";

        var firstChoice = result.Choices?.FirstOrDefault();
        if (firstChoice is JsonElement chEl && chEl.ValueKind == JsonValueKind.Object)
        {
            if (chEl.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                finishReason = frEl.GetString();

            if (chEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                toolCalls = JsonSerializer.Deserialize<object>(tcEl.GetRawText(), JsonSerializerOptions.Web);

            if (chEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object)
            {
                if (msgEl.TryGetProperty("content", out var contentEl))
                    content = contentEl.ValueKind == JsonValueKind.String
                        ? contentEl.GetString()
                        : ChatMessageContentExtensions.ToText(contentEl) ?? contentEl.GetRawText();

                if (toolCalls is null && msgEl.TryGetProperty("tool_calls", out var mtcEl)
                    && mtcEl.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    toolCalls = JsonSerializer.Deserialize<object>(mtcEl.GetRawText(), JsonSerializerOptions.Web);
                }
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ],
            Usage = null
        };

        var deltaObj = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(content))
            deltaObj["content"] = content;
        if (toolCalls is not null)
            deltaObj["tool_calls"] = toolCalls;

        if (deltaObj.Count > 0)
        {
            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new { index = 0, delta = deltaObj, finish_reason = (string?)null }
                ],
                Usage = null
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = finishReason ?? "stop" }
            ],
            Usage = result.Usage
        };
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!IsDeepResearchModel(chatRequest.GetModelId()))
        {
            await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
                cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        // RelaxAI deep-research endpoint is non-streaming.
        // Simulate Vercel UI stream parts from one non-stream response.
        var payload = BuildDeepResearchPayload(chatRequest);
        var result = await _client.GetChatCompletion(
            JsonSerializer.SerializeToElement(payload),
            relativeUrl: "v1/deep-research",
            ct: cancellationToken);

        var id = string.IsNullOrWhiteSpace(result.Id)
            ? Guid.NewGuid().ToString("n")
            : result.Id;

        var (content, finishReason) = ExtractFirstChoice(result);

        if (!string.IsNullOrWhiteSpace(content))
        {
            yield return id.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart
            {
                Id = id,
                Delta = content
            };
            yield return id.ToTextEndUIMessageStreamPart();
        }

        var (promptTokens, completionTokens, totalTokens) = ExtractUsage(result.Usage);

        yield return (finishReason ?? "stop").ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);
    }

    private static bool IsDeepResearchModel(string? model)
        => NormalizeModelId(model).EndsWith("-deep-research", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var value = model.Trim();
        if (value.Contains('/', StringComparison.Ordinal))
            value = value.SplitModelId().Model;

        return value;
    }

    private static string GetBaseModelForDeepResearch(string model)
    {
        var normalized = NormalizeModelId(model);
        return normalized.EndsWith("-deep-research", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^"-deep-research".Length]
            : normalized;
    }

    private static Dictionary<string, object?> BuildDeepResearchPayload(ChatCompletionOptions options)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = GetBaseModelForDeepResearch(options.Model),
            ["messages"] = options.Messages,
            ["temperature"] = options.Temperature,
            ["tools"] = options.Tools,
            ["tool_choice"] = options.ToolChoice,
            ["response_format"] = options.ResponseFormat,
            ["stream"] = false
        };

        return payload;
    }

    private static Dictionary<string, object?> BuildDeepResearchPayload(ChatRequest chatRequest)
    {
        var messages = chatRequest.Messages.ToCompletionMessages();

        var tools = chatRequest.Tools?.Select(a => new
        {
            type = "tool_type",
            function = new
            {
                name = a.Name,
                description = a.Description,
                parameters = a.InputSchema
            }
        }) ?? null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = GetBaseModelForDeepResearch(chatRequest.GetModelId()),
            ["temperature"] = chatRequest.Temperature,
            ["messages"] = messages,
            ["stream"] = false
        };

        if (chatRequest.MaxOutputTokens is not null)
            payload["max_tokens"] = chatRequest.MaxOutputTokens;

        if (chatRequest.ToolChoice is not null)
            payload["tool_choice"] = chatRequest.ToolChoice;

        if (chatRequest.ResponseFormat is null)
        {
            if (tools?.Any() != true)
                payload["response_format"] = new { type = "text" };
        }
        else
        {
            payload["response_format"] = chatRequest.ResponseFormat;
        }

        if (tools?.Any() == true)
            payload["tools"] = tools;

        return payload;
    }

    private static (string? content, string? stopReason) ExtractFirstChoice(ChatCompletion result)
    {
        if (result.Choices is null)
            return (null, "stop");

        string? content = null;
        string? stopReason = null;

        var firstChoice = result.Choices.FirstOrDefault();
        if (firstChoice is JsonElement choiceEl && choiceEl.ValueKind == JsonValueKind.Object)
        {
            if (choiceEl.TryGetProperty("finish_reason", out var frEl)
                && frEl.ValueKind == JsonValueKind.String)
            {
                stopReason = frEl.GetString();
            }

            if (choiceEl.TryGetProperty("message", out var msgEl)
                && msgEl.ValueKind == JsonValueKind.Object
                && msgEl.TryGetProperty("content", out var contentEl))
            {
                content = contentEl.ValueKind == JsonValueKind.String
                    ? contentEl.GetString()
                    : ChatMessageContentExtensions.ToText(contentEl) ?? contentEl.GetRawText();
            }
        }

        return (content, stopReason ?? "stop");
    }

    private static (int promptTokens, int completionTokens, int totalTokens) ExtractUsage(object? usage)
    {
        if (usage is not JsonElement usageEl || usageEl.ValueKind != JsonValueKind.Object)
            return (0, 0, 0);

        var promptTokens = usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number
            ? pt.GetInt32()
            : 0;

        var completionTokens = usageEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number
            ? ct.GetInt32()
            : 0;

        var totalTokens = usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number
            ? tt.GetInt32()
            : 0;

        return (promptTokens, completionTokens, totalTokens);
    }
}
