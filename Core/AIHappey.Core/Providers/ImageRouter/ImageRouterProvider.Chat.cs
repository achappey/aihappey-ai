using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ImageRouter;

public partial class ImageRouterProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        if (string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var part in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return part;

            yield break;
        }

        throw new NotSupportedException($"ImageRouter stream is only supported for image models. Model type '{model.Type}' is not supported.");
    }
}
