using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider
{
    private async Task<ChatCompletion> CompleteChatInternalAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        NormalizeParallelChatCompletionOptions(options);

        return await this.GetChatCompletion(
            _client,
            options,
            relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingInternalAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        NormalizeParallelChatCompletionOptions(options);

        options.Stream = true;

        await foreach (var update in this.GetChatCompletions(
                           _client,
                           options,
                           relativeUrl: ChatCompletionsPath,
                           cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private static void NormalizeParallelChatCompletionOptions(ChatCompletionOptions options)
    {
        options.Model = NormalizeParallelChatCompletionModel(options.Model);
        options.Messages = options.Messages.Select(message => new ChatMessage
        {
            Role = NormalizeRole(message.Role),
            Content = JsonSerializer.SerializeToElement(FlattenCompletionMessageContent(message.Content), Json)
        }).ToList();
    }

    private static string NormalizeParallelChatCompletionModel(string model)
        => string.IsNullOrWhiteSpace(model)
            ? model
            : model.StartsWith("parallel/", StringComparison.OrdinalIgnoreCase)
                ? model["parallel/".Length..]
                : model;
}
