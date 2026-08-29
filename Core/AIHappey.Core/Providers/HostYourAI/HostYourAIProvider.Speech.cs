using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;


namespace AIHappey.Core.Providers.HostYourAI;

public partial class HostYourAIProvider
{
    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();


    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleStreamingSpeechAsync(
            options,
            cancellationToken: cancellationToken);
    }

}
