using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Eachlabs;

public partial class EachlabsProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
