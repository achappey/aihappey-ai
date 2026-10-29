using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateGoogleOpenAISpeech(options);
        var speechResponse = await SpeechRequest(ToGoogleSpeechRequest(options), cancellationToken);
        return speechResponse.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateGoogleOpenAISpeech(options);
        var payload = BuildOpenAISpeechStreamingPayload(options, new List<object>());
        var state = new GoogleSpeechStreamState(payload);
        using var message = CreateGoogleSpeechStreamMessage(payload);
        using var response = await SendGoogleSpeechStreamAsync(message, cancellationToken);
        await foreach (var item in ReadGoogleSpeechEventsAsync(response, state, cancellationToken))
        {
            foreach (var audio in item.Audio)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (audio.Data.Length > 0)
                    yield return new AudioSpeechStreamDelta { Audio = audio.Data };
            }
            if (item.Type == "interaction.completed")
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AudioSpeechStreamDone
                {
                    Usage = state.Usage is { } usage ? new AudioSpeechUsage
                    {
                        InputTokens = checked((int?)GoogleSpeechInputTokens(usage)),
                        OutputTokens = checked((int?)GoogleSpeechOutputTokens(usage)),
                        TotalTokens = checked((int?)GoogleSpeechCount(usage, "total_tokens", "totalTokens", "total_token_count", "totalTokenCount"))
                    } : null
                };
            }
        }
    }

    private static void ValidateGoogleOpenAISpeech(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
    }

    private static SpeechRequest ToGoogleSpeechRequest(AudioSpeechRequest options)
    {
        var request = options.ToSpeechRequest();
        // Native input, annotations, multi-speaker speech_config and future fields
        // must survive the OpenAI media endpoint's extension-data passthrough.
        request.ProviderOptions = options.AdditionalProperties is null ? null : new()
        {
            [GoogleExtensions.Identifier()] = JsonSerializer.SerializeToElement(options.AdditionalProperties)
        };
        return request;
    }

    private static JsonObject BuildOpenAISpeechStreamingPayload(AudioSpeechRequest options, ICollection<object> warnings)
    {
        var payload = BuildSpeechPayload(ToGoogleSpeechRequest(options), warnings);
        if (!string.IsNullOrWhiteSpace(options.ResponseFormat))
            ApplyGoogleStreamingFormat(payload, new AudioFormat { Type = options.ResponseFormat }, warnings);
        payload["stream"] = true;
        return payload;
    }
}
