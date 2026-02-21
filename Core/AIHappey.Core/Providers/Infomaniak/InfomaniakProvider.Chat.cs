using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var relativeUrl = await GetChatCompletionsRelativeUrlAsync(cancellationToken);

        await foreach (var update in StreamCustomAsync(chatRequest,
            relativeUrl,
            cancellationToken))
            yield return update;
    }
}
