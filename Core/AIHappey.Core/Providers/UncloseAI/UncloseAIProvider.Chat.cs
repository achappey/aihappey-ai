using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.UncloseAI;

public partial class UncloseAIProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var route = ResolveRoute(chatRequest.Model);
        var request = CloneRequestWithModel(chatRequest, route.UpstreamModelId);

        await foreach (var update in _client.CompletionsStreamAsync(request,
            url: BuildUrl(route.BaseUri, ChatCompletionsPath),
            cancellationToken: cancellationToken))
            yield return update;
    }
}
