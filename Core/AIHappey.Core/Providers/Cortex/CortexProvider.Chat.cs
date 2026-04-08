using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cortex;

public partial class CortexProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var route = ResolveRoute(chatRequest.Model);
        var request = CloneWithModel(chatRequest, route.ModelId);

        await foreach (var update in _client.CompletionsStreamAsync(request,
            url: GetBackendUrl(route.Backend, "v1/chat/completions"),
            cancellationToken: cancellationToken))
            yield return update;
    }
}
