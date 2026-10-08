using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions chatRequest,
     CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        var response = result.ToChatCompletion();
        response.AdditionalProperties = AddAnthropicChatCompletionCost(
            response.AdditionalProperties, GetAnthropicGatewayCost(result.Metadata));
        return response;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
     [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {

            var update = part.ToChatCompletionUpdate();
            if (part.Event.Type == "finish" && part.Event.Data is AIFinishEventData finish)
                update.AdditionalProperties = AddAnthropicChatCompletionCost(
                    update.AdditionalProperties, finish.MessageMetadata?.Gateway?.Cost ?? GetAnthropicGatewayCost(part.Metadata));
            yield return update;

        }

        yield break;
    }

}
