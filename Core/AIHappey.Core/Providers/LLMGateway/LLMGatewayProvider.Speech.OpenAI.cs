using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LLMGateway;

public partial class LLMGatewayProvider
{
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            endpoint: "v1/audio/speech",
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // LLM Gateway currently exposes a non-streaming OpenAI-compatible speech
        // response. Reuse the provider's normal speech method and adapt that
        // response to the standard OpenAI speech stream event contract.
        return this.SpeechStreamingAsync(options, cancellationToken);
    }
}
