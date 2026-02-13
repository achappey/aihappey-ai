using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.Contracts;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

public static class ModelProviderChatCompletionsSamplingExtensions
{
    /// <summary>
    /// Minimal sampling handler that routes MCP Sampling requests through ChatCompletions.
    /// - Maps Sampling messages to ChatCompletionOptions (text-only).
    /// - Executes IModelProvider.CompleteChatAsync.
    /// - Returns CreateMessageResult with model/role/stop reason/text content.
    /// </summary>
    public static async Task<CreateMessageResult> ChatCompletionsSamplingAsync(
        this IModelProvider modelProvider,
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = chatRequest.GetModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new Exception("No model provided.");

        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(chatRequest.SystemPrompt))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = JsonSerializer.SerializeToElement(chatRequest.SystemPrompt)
            });
        }

        messages.AddRange(chatRequest.Messages.Select(ToChatMessage));

        var options = new ChatCompletionOptions
        {
            Model = model,
            Temperature = chatRequest.Temperature,
            Messages = messages
        };

        var result = await modelProvider.CompleteChatAsync(options, cancellationToken);
        var (content, stopReason) = ExtractFirstChoice(result);

        return new CreateMessageResult
        {
            Model = string.IsNullOrWhiteSpace(result.Model) ? model : result.Model,
            StopReason = stopReason ?? "stop",
            Content = [(content ?? string.Empty).ToTextContentBlock()],
            Role = Role.Assistant
        };
    }

    private static ChatMessage ToChatMessage(SamplingMessage samplingMessage)
    {
        var role = samplingMessage.Role switch
        {
            Role.User => "user",
            Role.Assistant => "assistant",
            _ => throw new NotSupportedException($"Unsupported role: {samplingMessage.Role}")
        };

        var text = samplingMessage.ToText() ?? string.Empty;

        return new ChatMessage
        {
            Role = role,
            Content = JsonSerializer.SerializeToElement(
            Enumerable.Select<ContentBlock, object>(
                samplingMessage.Content,
                a => a switch
                {
                    TextContentBlock t => new
                    {
                        type = "text",
                        text = t.Text
                    },
                    ImageContentBlock i => new
                    {
                        type = "image_url",
                        image_url = new
                        {
                            url = i.Data
                        }
                    },
                    AudioContentBlock i => new
                    {
                        type = "input_audio",
                        input_audio = new
                        {
                            data = i.Data,
                            format = i.MimeType.Split("/").LastOrDefault()
                        }
                    },
                    _ => throw new NotSupportedException(a.GetType().Name)
                }
            )
        )

        };
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
}
