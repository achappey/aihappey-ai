using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
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
