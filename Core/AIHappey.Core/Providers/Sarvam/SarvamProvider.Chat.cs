using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model))
            ?? throw new ArgumentException(chatRequest.Model);

        if (model.Type == "transcription")
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "speech")
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Id.Contains("translate"))
        {
            ApplyAuthHeader();
            
            await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        Dictionary<string, object?> payload = [];

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
