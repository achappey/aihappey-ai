using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PrunaAI;

public partial class PrunaAIProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
