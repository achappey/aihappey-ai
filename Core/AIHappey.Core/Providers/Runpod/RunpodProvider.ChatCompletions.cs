using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var messages = ToRunpodMessages(options.Messages);

        using var doc = await RunSyncAdaptiveAsync(
            model: options.Model,
            messages: messages,
            temperature: options.Temperature,
            maxTokens: null,
            topP: null,
            topK: null,
            cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? Guid.NewGuid().ToString("n")
            : Guid.NewGuid().ToString("n");

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (text, promptTokens, completionTokens) = ExtractRunpodTextAndUsage(root);

        object? usage = null;
        if (promptTokens is not null || completionTokens is not null)
        {
            var pt = promptTokens ?? 0;
            var ct = completionTokens ?? 0;
            usage = new
            {
                prompt_tokens = pt,
                completion_tokens = ct,
                total_tokens = pt + ct
            };
        }

        return new ChatCompletion
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    finish_reason = "stop",
                    message = new
                    {
                        role = "assistant",
                        content = text
                    }
                }
            ],
            Usage = usage
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completion = await CompleteChatAsync(options, cancellationToken).ConfigureAwait(false);
        var content = string.Empty;

        var firstChoice = completion.Choices.FirstOrDefault();
        if (firstChoice is JsonElement choiceEl
            && choiceEl.ValueKind == JsonValueKind.Object
            && choiceEl.TryGetProperty("message", out var messageEl)
            && messageEl.ValueKind == JsonValueKind.Object
            && messageEl.TryGetProperty("content", out var contentEl)
            && contentEl.ValueKind == JsonValueKind.String)
        {
            content = contentEl.GetString() ?? string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            yield return new ChatCompletionUpdate
            {
                Id = completion.Id,
                Object = "chat.completion.chunk",
                Created = completion.Created,
                Model = completion.Model,
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { content },
                        finish_reason = (string?)null
                    }
                ]
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = completion.Id,
            Object = "chat.completion.chunk",
            Created = completion.Created,
            Model = completion.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ],
            Usage = completion.Usage
        };
    }
}
