using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<FireworksProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = new()
        {
            ["reasoning_effort"] = metadata?.ReasoningEffort ?? "medium",
        };

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }
}