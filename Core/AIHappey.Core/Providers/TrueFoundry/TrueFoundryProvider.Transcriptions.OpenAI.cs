using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            endpoint: "audio/transcriptions",
            cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            endpoint: "audio/transcriptions",
            cancellationToken);
    }
}
