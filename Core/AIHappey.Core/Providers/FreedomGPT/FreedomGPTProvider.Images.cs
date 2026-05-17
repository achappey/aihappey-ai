using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;

namespace AIHappey.Core.Providers.FreedomGPT;

public partial class FreedomGPTProvider
{
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
}
