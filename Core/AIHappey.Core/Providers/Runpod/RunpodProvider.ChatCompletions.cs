using System.Text.Json;
using AIHappey.ChatCompletions.Models;

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
            usage = new ChatCompletionUsage { PromptTokens = pt, CompletionTokens = ct, TotalTokens = pt + ct };
        }

        return new ChatCompletion
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices =
            [
                new ChatCompletionChoice { Index = 0, FinishReason = "stop", Message = new ChatCompletionResponseMessage { Role = "assistant", Content = text } }
            ],
            Usage = (usage).ToChatCompletionUsage()
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var completion = await CompleteChatAsync(options, cancellationToken);
        var content = string.Empty;

        var firstChoice = completion.Choices.FirstOrDefault();
        content = firstChoice?.Message.Content ?? string.Empty;

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
                    new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Content = content }, FinishReason = (string?)null }
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
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = "stop" }
            ],
            Usage = completion.Usage
        };
    }
}
