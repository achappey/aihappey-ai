using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.YourVoic;

public partial class YourVoicProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is a required field", nameof(options));

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        // YourVoic's native stream is headerless PCM. The completed MP3/WAV
        // response is considerably easier and more reliable to play in browsers,
        // so adapt it to the OpenAI delta/done event contract.
        => this.SpeechStreamingAsync(options, cancellationToken);
}

