using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
     public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
         AudioSpeechRequest options,
         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAISpeechRequestAsync(options, cancellationToken);

        yield return new AudioSpeechStreamDelta
        {
            Audio = Convert.ToBase64String(response.Audio)
        };
        yield return new AudioSpeechStreamDone();
    }
}

