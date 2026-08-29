using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        return OpenAISpeechRequestCoreAsync(options, cancellationToken);
    }

    private async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestCoreAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken)
    {
        ValidateResembleOpenAISpeechRequest(options);

        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties is { Count: > 0 })
        {
            request.ProviderOptions = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(
                    options.AdditionalProperties,
                    SpeechJson)
            };
        }

        var response = await SpeechRequest(request, cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateResembleOpenAISpeechRequest(options);

        var responseFormat = NormalizeResembleOutputFormat(options.ResponseFormat);
        if (responseFormat is not null and not "wav")
        {
            throw new NotSupportedException(
                $"ResembleAI native streaming supports only wav response_format; received '{options.ResponseFormat}'.");
        }

        ApplyAuthHeader();
        var (_, modelVoiceUuid) = ParseSpeechModelAndVoice(options.Model);
        var voiceUuid = (modelVoiceUuid ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceUuid))
        {
            throw new ArgumentException(
                "ResembleAI requires a voice UUID in the model id or OpenAI voice field.",
                nameof(options));
        }

        var payload = options.AdditionalProperties is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : options.AdditionalProperties.ToDictionary(
                property => property.Key,
                property => (object?)property.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);

        // The streaming API selects the synthesis model from the voice. Required
        // canonical fields overwrite passthrough values deliberately.
        payload.Remove("output_format");
        payload["voice_uuid"] = voiceUuid;
        payload["data"] = options.Input;

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("https://f.cluster.resemble.ai/stream"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/wav"));

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"ResembleAI streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return new AudioSpeechStreamDone();
    }

    private static void ValidateResembleOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("'model' is a required field.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("'input' is a required field.", nameof(options));
    }
}

