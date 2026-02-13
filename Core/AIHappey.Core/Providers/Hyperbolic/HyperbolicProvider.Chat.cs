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

        var model = GetIdentifier().GetModels().FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model))
            ?? throw new ArgumentException(chatRequest.Model);

        if (model.Type == "image")
        {
            await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return update;

            yield break;
        }

        if (model.Type == "speech")
        {
            await foreach (var update in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return update;

            yield break;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
