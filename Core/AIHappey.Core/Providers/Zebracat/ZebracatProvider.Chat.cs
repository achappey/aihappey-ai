using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zebracat;

public partial class ZebracatProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
