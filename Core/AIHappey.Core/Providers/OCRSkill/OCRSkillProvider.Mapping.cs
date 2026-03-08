using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.OCRSkill;

internal static class OCRSkillProviderMapping
{
    public static ChatCompletionOptions ToChatCompletionOptions(this ResponseRequest options)
    {
        return new ChatCompletionOptions
        {
            Model = options.Model,
            Temperature = options.Temperature,
            Stream = options.Stream,
            ParallelToolCalls = options.ParallelToolCalls ?? true,
            ResponseFormat = options.Text,
            Messages = BuildCompletionMessages(options)
        };
    }

    private static List<ChatMessage> BuildCompletionMessages(ResponseRequest options)
    {
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(options.Instructions))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = JsonSerializer.SerializeToElement(options.Instructions)
            });
        }

        if (options.Input?.Text is { } text && !string.IsNullOrWhiteSpace(text))
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(text)
            });
        }

        var items = options.Input?.Items;
        if (items is null)
            return messages;

        foreach (var item in items.OfType<ResponseInputMessage>())
        {
            var role = item.Role switch
            {
                ResponseRole.System => "system",
                ResponseRole.Developer => "system",
                ResponseRole.Assistant => "assistant",
                _ => "user"
            };

            var parts = ToCompletionContent(item.Content).ToList();
            if (parts.Count == 0)
                continue;

            messages.Add(new ChatMessage
            {
                Role = role,
                Content = parts.Count == 1 && parts[0] is string singleText
                    ? JsonSerializer.SerializeToElement(singleText)
                    : JsonSerializer.SerializeToElement(parts)
            });
        }

        return messages;
    }

    private static IEnumerable<object> ToCompletionContent(ResponseMessageContent content)
    {
        if (content.Text is { } text && !string.IsNullOrWhiteSpace(text))
        {
            yield return text;
            yield break;
        }

        foreach (var part in content.Parts ?? [])
        {
            switch (part)
            {
                case InputTextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    yield return new { type = "text", text = textPart.Text };
                    break;

                case InputImagePart imagePart when !string.IsNullOrWhiteSpace(imagePart.ImageUrl):
                    yield return new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = imagePart.ImageUrl,
                            detail = imagePart.Detail
                        }
                    };
                    break;

                case InputFilePart filePart when !string.IsNullOrWhiteSpace(filePart.FileData):
                    yield return new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = filePart.FileData
                        }
                    };
                    break;
            }
        }
    }

    public static ResponseResult ToResponseResult(this ChatCompletion completion, ResponseRequest request)
    {
        var text = ExtractAssistantTextFromChoices(completion.Choices);
        var finishReason = ExtractFinishReason(completion.Choices) ?? "stop";
        var isCompleted = string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase);

        return new ResponseResult
        {
            Id = completion.Id ?? $"resp_{Guid.NewGuid():n}",
            Object = "response",
            CreatedAt = completion.Created,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model ?? completion.Model,
            Temperature = request.Temperature,
            Metadata = request.Metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = completion.Usage,
            Error = isCompleted ? null : new ResponseResultError { Code = finishReason, Message = $"Chat completion finished with reason '{finishReason}'." },
            Output =
            [
                new
                {
                    id = $"msg_{completion.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    public static ResponseResult ToResponseResult(this ChatCompletionUpdate update, ResponseRequest request, string responseId, string itemId, long createdAt, string finishReason)
    {
        var text = TryGetDeltaText(update) ?? string.Empty;
        var isCompleted = string.Equals(finishReason, "stop", StringComparison.OrdinalIgnoreCase);

        return new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model ?? update.Model,
            Temperature = request.Temperature,
            Metadata = request.Metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = update.Usage,
            Error = isCompleted ? null : new ResponseResultError { Code = finishReason, Message = $"Chat completion finished with reason '{finishReason}'." },
            Output =
            [
                new
                {
                    id = itemId,
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    public static string? TryGetDeltaText(ChatCompletionUpdate update)
    {
        var choicesJson = JsonSerializer.SerializeToElement(update.Choices ?? Array.Empty<object>());
        foreach (var choice in choicesJson.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta))
                continue;

            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
        }

        return null;
    }

    public static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        var choicesJson = JsonSerializer.SerializeToElement(update.Choices ?? Array.Empty<object>());
        foreach (var choice in choicesJson.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var finishReason) && finishReason.ValueKind == JsonValueKind.String)
                return finishReason.GetString();
        }

        return null;
    }

    private static string ExtractAssistantTextFromChoices(IEnumerable<object>? choices)
    {
        if (choices is null)
            return string.Empty;

        var choicesJson = JsonSerializer.SerializeToElement(choices);
        foreach (var choice in choicesJson.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message))
                continue;

            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? ExtractFinishReason(IEnumerable<object>? choices)
    {
        if (choices is null)
            return null;

        var choicesJson = JsonSerializer.SerializeToElement(choices);
        foreach (var choice in choicesJson.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out var finishReason) && finishReason.ValueKind == JsonValueKind.String)
                return finishReason.GetString();
        }

        return null;
    }

    public static string ExtractAssistantText(IEnumerable<object>? output)
    {
        if (output is null)
            return string.Empty;

        var root = JsonSerializer.SerializeToElement(output);
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}
