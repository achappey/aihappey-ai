using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ElectronHub;

public partial class ElectronHubProvider
{
  
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
