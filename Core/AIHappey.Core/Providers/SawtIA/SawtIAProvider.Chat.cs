using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SawtIA;

public partial class SawtIAProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
