using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.XiaomiMIMO;

public partial class XiaomiMIMOProvider
{
    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
       => throw new NotSupportedException();
}
