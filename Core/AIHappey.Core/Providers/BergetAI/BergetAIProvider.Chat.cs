using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BergetAI;

public partial class BergetAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var modelId = chatRequest.Model ?? throw new Exception("Model missing");

        if (modelId.Contains("whisper", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var update in this.StreamTranscriptionAsync(chatRequest,
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
