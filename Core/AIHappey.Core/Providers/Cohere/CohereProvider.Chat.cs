using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var update in StreamUnifiedAsync(
                                 request,
                                  cancellationToken: cancellationToken))
        {

            foreach (var result in update.Event.ToUIMessagePart(GetIdentifier()))
                yield return result;
        }
    }
}
