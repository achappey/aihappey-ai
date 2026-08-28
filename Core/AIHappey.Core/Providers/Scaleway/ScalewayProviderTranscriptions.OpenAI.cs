using AIHappey.Core.AI;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Scaleway;
using System.Globalization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Scaleway;

public partial class ScalewayProvider
{

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            cancellationToken: cancellationToken);
    }
}
