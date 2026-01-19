using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.SambaNova;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.SambaNova;

public partial class SambaNovaProvider : IModelProvider
{
    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        if (chatRequest.Model.Contains("whisper"))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        ApplyAuthHeader();

        var metadata = chatRequest.GetProviderMetadata<SambaNovaProviderMetadata>(GetIdentifier());

        Dictionary<string, object?> payload = [];

        if (!string.IsNullOrEmpty(metadata?.ReasoningEffort))
        {
            payload["reasoning_effort"] = metadata?.ReasoningEffort;
        }

        if (metadata?.ParallelToolCalls.HasValue == true)
        {
            payload["parallel_tool_calls"] = metadata?.ParallelToolCalls;
        }

        if (metadata?.ChatTemplateKwargs != null)
        {
            payload["chat_template_kwargs"] = metadata?.ChatTemplateKwargs;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            payload,
            cancellationToken: cancellationToken))
            yield return update;
    }
}
