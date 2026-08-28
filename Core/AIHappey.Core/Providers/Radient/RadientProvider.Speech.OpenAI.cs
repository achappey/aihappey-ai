using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Radient;

public partial class RadientProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var payload = CopyMetadata(options.AdditionalProperties);
        payload["model"] = StripProviderPrefix(options.Model);
        payload["input"] = options.Input;
        Set(payload, "voice", options.Voice);
        Set(payload, "response_format", options.ResponseFormat);
        Set(payload, "speed", options.Speed);
        var result = await SendSpeechAsync(payload, options.ResponseFormat, cancellationToken);
        return (result.Audio, result.MimeType);
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (audio, _) = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(audio) };
        yield return new AudioSpeechStreamDone();
    }
}
