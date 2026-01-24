using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OVHcloud;

public partial class OVHcloudProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsImageModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamImageAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        if (IsTranscriptionModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        if (IsSpeechModel(chatRequest.Model))
        {
            await foreach (var update in this.StreamSpeechAsync(chatRequest,
              cancellationToken: cancellationToken))
                yield return update;

            yield break;
        }

        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
