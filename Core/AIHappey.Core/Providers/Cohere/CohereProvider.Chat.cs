using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(m => m.Id == chatRequest.Model);

        if (model?.Type == "transcription")
        {
            await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
                cancellationToken: cancellationToken))
            {
                yield return update;
            }

            yield break;
        }

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
