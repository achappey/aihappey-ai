using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
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
