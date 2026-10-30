using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToSpeech(options), cancellationToken)).ToOpenAISpeechAudio();

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StreamFormat is not null and not "sse")
            throw new NotSupportedException("KalpaLabs OpenAI speech streaming supports stream_format=sse only.");
        var plan = PrepareSpeech(ToSpeech(options), streaming: true);
        await foreach (var chunk in StreamSpeech(plan, cancellationToken))
        {
            if (chunk.Audio is not null)
                yield return new AudioSpeechStreamDelta { Audio = chunk.Audio };
            if (chunk.Kind == "end")
                yield return new AudioSpeechStreamDone();
        }
    }

    private static SpeechRequest ToSpeech(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties?.TryGetValue("providerOptions", out var providerOptions) == true)
            request.ProviderOptions = providerOptions.Deserialize<Dictionary<string, JsonElement>>(Json);
        else if (options.AdditionalProperties?.TryGetValue(ProviderId, out var nativeOptions) == true)
            request.ProviderOptions = new() { [ProviderId] = nativeOptions.Clone() };
        else if (options.AdditionalProperties is { Count: > 0 })
            request.ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        return request;
    }
}
