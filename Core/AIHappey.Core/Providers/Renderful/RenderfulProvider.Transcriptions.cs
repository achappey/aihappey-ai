using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Renderful;

public partial class RenderfulProvider
{

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Renderful speech-to-text requires a public audio_url, but Renderful has not documented an upload contract or a completed-task transcript payload that can safely map AIHappey audio inputs.");



    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Renderful speech-to-text is not available until its upload and transcript-result contracts are documented.");
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Renderful speech-to-text streaming is not available until its upload and transcript-result contracts are documented.");
    }
}
