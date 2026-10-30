using System.Runtime.CompilerServices;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        return (await SpeechRequest(options.ToSpeechRequest(), cancellationToken)).ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        if (string.IsNullOrWhiteSpace(options.Voice))
            throw new ArgumentException("FishAudio voice is required. Provide AudioSpeechRequest.voice with a FishAudio reference_id.", nameof(options));
        await foreach (var part in StreamFishSpeechAsync(new StreamingSpeechRequest
        {
            Model = options.Model, Text = [new() { Text = options.Input }], Voice = options.Voice.Trim(), Speed = options.Speed,
            OutputFormat = options.ResponseFormat is null ? null : new() { Type = options.ResponseFormat }
        }, cancellationToken))
        {
            if (part is SpeechAudioPart audio && audio.Audio.Length > 0) yield return new AudioSpeechStreamDelta { Audio = audio.Audio };
            if (part is SpeechFinishPart) yield return new AudioSpeechStreamDone();
        }
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
    }
}
