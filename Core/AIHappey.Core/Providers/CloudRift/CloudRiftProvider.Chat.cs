using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.CloudRift;

public sealed partial class CloudRiftProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // POST https://inference.cloudrift.ai/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }
}
