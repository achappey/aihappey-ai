using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Novita;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider
{

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var models = StaticModels(GetIdentifier());
        var model = models.FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model));

        if (model != null)
        {
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

            if (model.Type == "image")
            {
                await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                    yield return p;

                yield break;
            }
        }

        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<NovitaProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = new()
        {
            ["enable_thinking"] = metadata?.EnableThinking,
            ["separate_reasoning"] = metadata?.SeparateReasoning
        };

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }

}
