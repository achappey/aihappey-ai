using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EvoLinkAI;

public partial class EvoLinkAIProvider
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

        var response = await EvoLinkAISpeechRequest(new SpeechRequest
        {
            Model = options.Model,
            Text = options.Input,
            Voice = options.Voice,
            OutputFormat = options.ResponseFormat,
            Instructions = options.Instructions,
            Speed = options.Speed
        }, cancellationToken);

        var audio = response.Audio
            ?? throw new InvalidOperationException("EvoLinkAI speech returned no audio.");
        return (Convert.FromBase64String(audio.Base64), audio.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }

}
