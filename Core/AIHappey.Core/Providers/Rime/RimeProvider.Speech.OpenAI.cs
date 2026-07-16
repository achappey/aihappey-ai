using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Rime;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
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

        var (payload, acceptMimeType) = BuildOpenAISpeechStreamingRequest(options);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rime-tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RimeSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.ParseAdd(acceptMimeType);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
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

    private static (Dictionary<string, object?> Payload, string AcceptMimeType) BuildOpenAISpeechStreamingRequest(AudioSpeechRequest options)
    {
        var (modelId, modelVoice) = ParseModelAndVoice(options.Model);

        if (!BaseModels.Contains(modelId, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{modelId}' is not supported.");

        var voice = !string.IsNullOrWhiteSpace(modelVoice)
            ? modelVoice
            : options.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Rime voice is required. Use a voice-expanded model id or provide voice for base models.", nameof(options));

        var audioFormat = NormalizeOutputFormat(options.ResponseFormat);
        var acceptMimeType = ResolveAcceptMimeType(audioFormat);
        var samplingRate = ResolveSamplingRate(audioFormat, samplingRate: null);
        var isMistV2 = modelId.Equals("mistv2", StringComparison.OrdinalIgnoreCase);
        var isNewStreamingModel = NewStreamingModels.Contains(modelId);

        var payload = BuildStreamingSpeechPayload(
            modelId,
            voice,
            options.Input,
            ResolveLanguage(modelId, language: null),
            samplingRate,
            isMistV2 ? 1.0f : null,
            isNewStreamingModel ? null : null,
            metadata: null);

        return (payload, acceptMimeType);
    }
}
