using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Baidu;

public partial class BaiduProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "v3/chat/completions",
            cancellationToken: cancellationToken))
            yield return update;
    }
}
