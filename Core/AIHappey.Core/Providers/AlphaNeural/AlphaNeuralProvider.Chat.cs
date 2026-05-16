using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.AlphaNeural;

public partial class AlphaNeuralProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        if (string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var part in this.StreamImageAsync(chatRequest, cancellationToken))
            {
                yield return part;
            }

            yield break;
        }

        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
    }
}
