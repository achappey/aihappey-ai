using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.JigsawStack;

public partial class JigsawStackProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => ExtractChatMessageTexts((m.Content).ToJsonElement()))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var executed = await ExecuteModelAsync(modelId, texts, metadata: null, cancellationToken);

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId.ToModelId(GetIdentifier()),
            Choices =
            [
                new ChatCompletionChoice { Index = 0, Message = new ChatCompletionResponseMessage { Role = "assistant", Content = executed.Text }, FinishReason = "stop" }
            ],
            Usage = null
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => ExtractChatMessageTexts((m.Content).ToJsonElement()))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId.ToModelId(GetIdentifier()),
            Choices =
            [
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Role = "assistant" }, FinishReason = (string?)null }
            ]
        };

        var executed = await ExecuteModelAsync(modelId, texts, metadata: null, cancellationToken);

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId.ToModelId(GetIdentifier()),
            Choices =
            [
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Content = executed.Text }, FinishReason = (string?)null }
            ]
        };

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = modelId.ToModelId(GetIdentifier()),
            Choices =
            [
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = "stop" }
            ]
        };
    }


}
