using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Hyperbolic;

public partial class HyperbolicProvider 
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (HyperbolicImageModels.Any(a => a.Id.Equals($"{GetIdentifier()}/{chatRequest.Model}")))
            await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return update;
        else if (HyperbolicSpeechModels.Any(a => a.Id.Equals($"{GetIdentifier()}/{chatRequest.Model}")))
            await foreach (var update in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return update;
        else
            await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
                cancellationToken: cancellationToken))
                yield return update;
    }
}