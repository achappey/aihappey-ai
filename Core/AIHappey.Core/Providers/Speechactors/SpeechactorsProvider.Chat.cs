using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechactors;

public partial class SpeechactorsProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
