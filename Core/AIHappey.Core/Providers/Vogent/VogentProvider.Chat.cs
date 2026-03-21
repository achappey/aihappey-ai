using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vogent;

public partial class VogentProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
       throw new NotImplementedException();
    }
}
