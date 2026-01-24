using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.GTranslate;

public partial class GTranslateProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
            yield return p;
    }
}
