using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.JassieAI;

public partial class JassieAIProvider
{
    private async Task<ChatCompletion> CompleteChatInternalAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var payload = BuildNativeRequest(options, stream: false);
        var endpoint = ResolveEndpoint(payload.Model);
        var native = await SendNativeAsync(payload, endpoint, cancellationToken);

        return new ChatCompletion
        {
            Id = native.RequestId ?? Guid.NewGuid().ToString("N"),
            Object = "chat.completion",
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = payload.Model,
            Usage = (BuildUsage(native)).ToChatCompletionUsage(),
            Choices =
            [
                new ChatCompletionChoice { Index = native.Index ?? 0, FinishReason = string.Equals(native.Type, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "stop", Message = new ChatCompletionResponseMessage { Role = "assistant", Content = native.Content ?? string.Empty } }
            ]
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildNativeRequest(options, stream: true);
        var endpoint = ResolveEndpoint(payload.Model);

        await foreach (var chunk in StreamNativeAsync(payload, endpoint, cancellationToken))
        {
            var content = chunk.Content ?? string.Empty;
            yield return new ChatCompletionUpdate
            {
                Id = chunk.RequestId ?? Guid.NewGuid().ToString("N"),
                Object = "chat.completion.chunk",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = payload.Model,
                Usage = (BuildUsage(chunk)).ToChatCompletionUsage(),
                Choices =
                [
                    new ChatCompletionStreamChoice { Index = chunk.Index ?? 0, Delta = new ChatCompletionDelta { Role = "assistant", Content = content }, FinishReason = string.Equals(chunk.Type, "error", StringComparison.OrdinalIgnoreCase) ? "error" : (string?)null }
                ]
            };
        }
    }
}
