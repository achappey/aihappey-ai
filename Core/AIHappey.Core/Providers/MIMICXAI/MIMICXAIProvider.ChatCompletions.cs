using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await ExecuteNativeChatCompletionAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteNativeChatCompletionStreamingAsync(options, cancellationToken);
    }

    private async Task<ChatCompletion> ExecuteNativeChatCompletionAsync(
           ChatCompletionOptions options,
           CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = BuildNativeTextRequest(options);
        var generated = await ExecuteNativeGenerateAsync(request, cancellationToken);

        return new ChatCompletion
        {
            Id = generated.Id,
            Object = "chat.completion",
            Created = generated.CreatedAt,
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = generated.Text
                    },
                    finish_reason = "stop"
                }
            ]
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> ExecuteNativeChatCompletionStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var request = BuildNativeTextRequest(options);
        var completionId = Guid.NewGuid().ToString("n");
        var sentRole = false;

        await foreach (var delta in ExecuteNativeStreamTextAsync(request, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(delta))
                continue;

            yield return new ChatCompletionUpdate
            {
                Id = completionId,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = options.Model,
                Choices =
                [
                    sentRole
                        ? new
                        {
                            index = 0,
                            delta = new
                            {
                                content = delta
                            }
                        }
                        : new
                        {
                            index = 0,
                            delta = new
                            {
                                role = "assistant",
                                content = delta
                            }
                        }
                ]
            };

            sentRole = true;
        }

        yield return new ChatCompletionUpdate
        {
            Id = completionId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
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
