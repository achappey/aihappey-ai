using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
