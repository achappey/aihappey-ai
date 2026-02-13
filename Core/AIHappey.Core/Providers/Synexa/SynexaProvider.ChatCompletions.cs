using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromChatCompletionOptions(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("No prompt content found in chat messages.");

        var prediction = await CreatePredictionAsync(
            options.Model,
            new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["temperature"] = options.Temperature
            },
            cancellationToken);

        var completed = await WaitPredictionAsync(prediction, wait: null, cancellationToken);
        var text = ExtractOutputText(completed.Output);

        return new ChatCompletion
        {
            Id = completed.Id,
            Object = "chat.completion",
            Created = ParseTimestampOrNow(completed.CreatedAt).ToUnixTimeSeconds(),
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
            Usage = completed.Metrics.ValueKind == JsonValueKind.Undefined || completed.Metrics.ValueKind == JsonValueKind.Null
                ? null
                : completed.Metrics.Clone()
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteChatAsync(options, cancellationToken);
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
            ]
        };
    }
}

