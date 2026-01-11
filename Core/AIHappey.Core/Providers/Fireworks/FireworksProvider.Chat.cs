using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Fireworks;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = FireworksModels.FirstOrDefault(a => a.Id == chatRequest.Model)
            ?? throw new ArgumentException(chatRequest.Model);

        if (model.Type == "transcription")
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "image")
        {
            await foreach (var p in this.StreamImageAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<FireworksProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = [];

        if (!string.IsNullOrEmpty(metadata?.ReasoningEffort))
        {
            payload["reasoning_effort"] = metadata?.ReasoningEffort;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
