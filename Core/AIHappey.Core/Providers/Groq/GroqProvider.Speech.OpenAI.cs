using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (audio.Length > 0)
        {
            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(audio)
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDone();
    }
}
