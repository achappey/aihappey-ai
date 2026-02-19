using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

}
