using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.WisGate;

public partial class WisGateProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model?.Type)
        {
            case "image":
                {
                    await foreach (var update in this.StreamImageAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;

                    yield break;
                }

            case "video":
                {
                    await foreach (var update in this.StreamVideoAsync(chatRequest,
                            cancellationToken: cancellationToken))
                        yield return update;

                    yield break;
                }

            default:
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
}
