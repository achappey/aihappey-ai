using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var response = await SpeechRequest(ToRimeSpeechRequest(options), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var plan = PrepareSpeech(ToRimeSpeechRequest(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var message = CreateSpeechMessage(plan);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        var (_, format, _) = ResolveResponseAudio(response, plan);
        await foreach (var chunk in ReadSpeechChunksAsync(response, format, cancellationToken))
        {
            if (chunk.Audio is not null)
                yield return new AudioSpeechStreamDelta { Audio = chunk.Audio };
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDone();
    }

    private static SpeechRequest ToRimeSpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        // OpenAI's extension-data surface can carry the same providerOptions as /api/speech.
        if (options.AdditionalProperties?.TryGetValue("providerOptions", out var providerOptions) == true)
            request.ProviderOptions = providerOptions.Deserialize<Dictionary<string, JsonElement>>(RimeSpeechJson);
        else if (options.AdditionalProperties?.TryGetValue(ProviderId, out var rimeOptions) == true)
            request.ProviderOptions = new() { [ProviderId] = rimeOptions.Clone() };
        return request;
    }
}
