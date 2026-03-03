using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.WisdomGate;

public partial class WisdomGateProvider
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
                ApplyAuthHeader();

                await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
                    cancellationToken: cancellationToken))
                    yield return update;

                yield break;
        }
    }
}
