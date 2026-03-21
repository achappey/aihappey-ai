using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

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
            Usage = BuildUsage(native),
            Choices =
            [
                new
                {
                    index = native.Index ?? 0,
                    finish_reason = string.Equals(native.Type, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
                    message = new
                    {
                        role = "assistant",
                        content = native.Content ?? string.Empty
                    }
                }
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
                Usage = BuildUsage(chunk),
                Choices =
                [
                    new
                    {
                        index = chunk.Index ?? 0,
                        delta = new
                        {
                            role = "assistant",
                            content
                        },
                        finish_reason = string.Equals(chunk.Type, "error", StringComparison.OrdinalIgnoreCase) ? "error" : (string?)null
                    }
                ]
            };
        }
    }
}
