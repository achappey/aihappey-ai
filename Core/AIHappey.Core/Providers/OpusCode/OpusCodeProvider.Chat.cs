using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpusCode;

public partial class OpusCodeProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
