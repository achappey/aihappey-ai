using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();
            return await CompleteResearchChatAsync(options, model, cancellationToken);
        }

        if (!IsAnswerModel(model))
            throw new NotSupportedException($"Exa chat completions only support model 'exa'. Requested: '{options.Model}'.");

        ApplyChatAuthHeader();
        return await _client.GetChatCompletion(options, relativeUrl: "chat/completions", ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();
            return CompleteResearchChatStreamingAsync(options, model, cancellationToken);
        }

        if (!IsAnswerModel(model))
            throw new NotSupportedException($"Exa chat completions only support model 'exa'. Requested: '{options.Model}'.");

        ApplyChatAuthHeader();
        return _client.GetChatCompletionUpdates(options, relativeUrl: "chat/completions", ct: cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteResearchChatStreamingAsync(
        ChatCompletionOptions options,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = BuildPromptFromCompletionMessages(options.Messages);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Exa research requires non-empty input derived from chat messages.");

        var outputSchema = TryExtractOutputSchema(options.ResponseFormat);

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, cancellationToken))
        {
            var update = ToChatCompletionUpdate(evt, model);
            if (update is not null)
                yield return update;
        }
    }

    private async Task<ChatCompletion> CompleteResearchChatAsync(
        ChatCompletionOptions options,
        string model,
        CancellationToken cancellationToken)
    {
        var input = BuildPromptFromCompletionMessages(options.Messages);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Exa research requires non-empty input derived from chat messages.");

        var outputSchema = TryExtractOutputSchema(options.ResponseFormat);
        var queued = await QueueResearchTaskAsync(input, model, outputSchema, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.ResearchId, cancellationToken);

        return ToChatCompletion(completed, model);
    }

    private static ChatCompletion ToChatCompletion(ExaResearchCompletedTask completed, string model)
    {
        var text = ToOutputText(completed.Parsed ?? completed.Content);

        return new ChatCompletion
        {
            Id = completed.ResearchId,
            Created = ToUnixTimeSeconds(completed.CreatedAt),
            Model = model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = text
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            }
        };
    }

    private static ChatCompletionUpdate? ToChatCompletionUpdate(ExaResearchStreamEvent evt, string fallbackModel)
    {
        var delta = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(evt.ContentText))
            delta["content"] = evt.ContentText;
        else if (evt.ContentObject is not null)
            delta["content"] = JsonSerializer.Deserialize<object>(evt.ContentObject.Value.GetRawText(), JsonSerializerOptions.Web);

        var finishReason = evt.IsTerminal ? (evt.Error is null ? "stop" : "error") : null;

        if (delta.Count == 0 && string.IsNullOrWhiteSpace(finishReason))
            return null;

        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["delta"] = delta
        };

        if (!string.IsNullOrWhiteSpace(finishReason))
            choice["finish_reason"] = finishReason;

        return new ChatCompletionUpdate
        {
            Id = string.IsNullOrWhiteSpace(evt.ResearchId) ? Guid.NewGuid().ToString("n") : evt.ResearchId,
            Created = evt.Created > 0 ? evt.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = fallbackModel,
            Choices = [choice]
        };
    }

    private static long ToUnixTimeSeconds(DateTime dateTime)
        => new DateTimeOffset(dateTime).ToUnixTimeSeconds();
}
