using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GreenPT;

public partial class GreenPTProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModelId();

        // Hard transcription routing by model id string, without model lookup.
        if (modelId.StartsWith("green-s", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        var messages = chatRequest.Messages
            .Select(m => new
            {
                Role = m.Role.ToString(),
                Text = string.Concat(m.Parts.OfType<TextUIPart>().Select(p => p.Text))
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = JsonSerializer.SerializeToElement(m.Text)
            })
            .ToList();

        if (messages.Count == 0)
        {
            yield return "No prompt provided.".ToErrorUIPart();
            yield break;
        }

        var options = new ChatCompletionOptions
        {
            Model = modelId,
            Temperature = chatRequest.Temperature,
            Stream = true,
            Messages = messages,
            ToolChoice = chatRequest.ToolChoice,
            ResponseFormat = chatRequest.ResponseFormat
        };

        string? streamId = null;
        bool started = false;

        int promptTokens = 0;
        int completionTokens = 0;
        int totalTokens = 0;

        await foreach (var update in ChatCompletionsCompleteChatStreamingAsync(options, cancellationToken))
        {
            streamId ??= string.IsNullOrWhiteSpace(update.Id)
                ? Guid.NewGuid().ToString("n")
                : update.Id;

            if (update.Usage is JsonElement usageEl && usageEl.ValueKind == JsonValueKind.Object)
            {
                if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                    promptTokens = pt.GetInt32();

                if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                    completionTokens = ct.GetInt32();

                if (usageEl.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                    totalTokens = tt.GetInt32();
            }

            foreach (var choiceObj in update.Choices ?? [])
            {
                if (choiceObj is not JsonElement choiceEl || choiceEl.ValueKind != JsonValueKind.Object)
                    continue;

                if (choiceEl.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.ValueKind == JsonValueKind.Object
                    && deltaEl.TryGetProperty("content", out var contentEl))
                {
                    var deltaText = ExtractText(contentEl);
                    if (!string.IsNullOrWhiteSpace(deltaText))
                    {
                        if (!started)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            started = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = deltaText
                        };
                    }
                }

                if (choiceEl.TryGetProperty("finish_reason", out var frEl)
                    && frEl.ValueKind == JsonValueKind.String)
                {
                    if (started)
                    {
                        yield return streamId.ToTextEndUIMessageStreamPart();
                        started = false;
                    }

                    yield return (frEl.GetString() ?? "stop").ToFinishUIPart(
                        chatRequest.Model,
                        outputTokens: completionTokens,
                        inputTokens: promptTokens,
                        totalTokens: totalTokens,
                        temperature: chatRequest.Temperature);
                    yield break;
                }
            }
        }

        if (started)
            yield return streamId!.ToTextEndUIMessageStreamPart();

        yield return "stop".ToFinishUIPart(
            chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: chatRequest.Temperature);

        static string? ExtractText(JsonElement content)
        {
            return content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Array => string.Concat(
                    content.EnumerateArray()
                        .Select(item => item.ValueKind == JsonValueKind.String
                            ? item.GetString()
                            : item.ValueKind == JsonValueKind.Object
                                && item.TryGetProperty("text", out var textEl)
                                && textEl.ValueKind == JsonValueKind.String
                                    ? textEl.GetString()
                                    : null)
                        .Where(s => !string.IsNullOrWhiteSpace(s))),
                JsonValueKind.Object when content.TryGetProperty("text", out var textObj)
                                           && textObj.ValueKind == JsonValueKind.String => textObj.GetString(),
                _ => null
            };
        }
    }
}
