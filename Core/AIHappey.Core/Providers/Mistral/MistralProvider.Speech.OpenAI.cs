using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is not null)
            request.ProviderOptions = new() { ["mistral"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        var warnings = new List<object>();
        var (payload, _, _) = await PrepareSpeechStreamPayloadAsync(request, warnings, cancellationToken);
        using var message = CreateSpeechStreamMessage(payload);
        using var response = await SendAudioStreamingAsync(message, cancellationToken);
        await foreach (var item in ReadAudioEventsAsync(response, "speech.audio.done", cancellationToken))
        {
            if (item.Type == "speech.audio.delta")
                yield return new AudioSpeechStreamDelta { Audio = RequiredAudioString(item.Payload, "audio_data") };
            else if (item.Type == "speech.audio.done")
            {
                var usage = ReadSpeechStreamUsage(item.Payload);
                yield return new AudioSpeechStreamDone
                {
                    Usage = usage is null ? null : new AudioSpeechUsage
                    {
                        InputTokens = checked((int?)usage.InputTokens),
                        OutputTokens = checked((int?)usage.OutputTokens),
                        TotalTokens = item.Payload.TryGetProperty("usage", out var raw)
                            ? checked((int?)AudioCount(raw, "total_tokens")) : null
                    }
                };
            }
        }
    }
}
