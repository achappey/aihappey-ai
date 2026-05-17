using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ApiAirforce;

public partial class ApiAirforceProvider
{

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
