using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.QuiverAI;

public partial class QuiverAIProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        return StreamChatCoreAsync(chatRequest, cancellationToken);
    }
}
