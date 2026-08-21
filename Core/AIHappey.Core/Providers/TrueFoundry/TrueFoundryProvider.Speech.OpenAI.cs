using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            endpoint: "audio/speech",
            cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleStreamingSpeechAsync(
            options,
            endpoint: "audio/speech",
            cancellationToken);
    }


}
