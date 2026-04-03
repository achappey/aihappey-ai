using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runtimo;

public partial class RuntimoProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
         throw new NotImplementedException();
    }
}
