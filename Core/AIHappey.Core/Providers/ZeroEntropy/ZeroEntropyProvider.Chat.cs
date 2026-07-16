using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZeroEntropy;

public partial class ZeroEntropyProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
