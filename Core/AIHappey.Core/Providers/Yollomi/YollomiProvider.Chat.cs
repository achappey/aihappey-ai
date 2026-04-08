using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Yollomi;

public partial class YollomiProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
         throw new NotImplementedException();
    }
}
