using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Sarvam;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);
        ApplyAuthHeader();

        var (payload, outputAudioCodec) = BuildSarvamOpenAISpeechPayload(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-speech/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        var acceptMimeType = GuessMimeType(outputAudioCodec) ?? "application/octet-stream";
        httpRequest.Headers.Accept.ParseAdd(acceptMimeType);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Sarvam streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private (Dictionary<string, object?> Payload, string? OutputAudioCodec) BuildSarvamOpenAISpeechPayload(AudioSpeechRequest options)
    {
        var request = options.ToSpeechRequest();
        var metadata = request.GetProviderMetadata<SarvamSpeechProviderMetadata>(GetIdentifier());
        var payload = BuildSarvamSpeechPayload(request, metadata);

        return (payload, NormalizeSarvamCodec(request.OutputFormat ?? metadata?.OutputAudioCodec));
    }
}
