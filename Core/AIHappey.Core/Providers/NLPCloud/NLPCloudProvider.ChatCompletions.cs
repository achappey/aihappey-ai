using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        var completion = response.ToChatCompletion();
        (completion.AdditionalProperties ??= [])["provider_metadata"] =
            JsonSerializer.SerializeToElement(response.Metadata, NativeJson);
        return completion;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
        {
            if (part.Event.Type == "text-end")
                continue;
            var update = part.ToChatCompletionUpdate();
            update.Model = options.Model;
            if (part.Event.Data is AITextStartEventData)
                update.Choices = [new ChatCompletionStreamChoice
                {
                    Index = 0, Delta = new ChatCompletionDelta { Role = "assistant" }, FinishReason = (string?)null
                }];
            else if (part.Event.Data is AITextDeltaEventData delta)
                update.Choices = [new ChatCompletionStreamChoice
                {
                    Index = 0, Delta = new ChatCompletionDelta { Content = delta.Delta }, FinishReason = (string?)null
                }];
            else if (part.Event.Data is AIFinishEventData finish)
                update.Usage = finish.MessageMetadata?.Usage.ToChatCompletionUsage();
            (update.AdditionalProperties ??= [])["provider_metadata"] =
                JsonSerializer.SerializeToElement(part.Metadata, NativeJson);
            yield return update;
        }
    }
}
