using System.Text;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider
{
    private sealed class ToolCallState
    {
        public string Id { get; }
        public string? Name { get; set; }
        public bool ProviderExecuted { get; set; }
        public StringBuilder InputJson { get; } = new();

        public ToolCallState(string id, string? name = null)
        {
            Id = id;
            Name = name;
        }
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                if (uiPart is FinishUIPart finishPart)
                {
                    var responseModel = finishPart.MessageMetadata?.Model;

                    var pricing = ResolveModelPricing(responseModel, chatRequest.Model);
                    yield return new FinishUIPart
                    {
                        FinishReason = finishPart.FinishReason,
                        MessageMetadata = ModelCostMetadataEnricher.AddCost(finishPart.MessageMetadata, pricing)
                    };
                    continue;
                }

                yield return uiPart;
            }
        }

        yield break;
  
    }

}
