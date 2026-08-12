using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Soniox;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Soniox;

public partial class SonioxProvider
{
    private static readonly JsonSerializerOptions SonioxJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<SonioxSpeechProviderMetadata>(GetIdentifier());
        var (model, modelVoice) = ParseSpeechModel(request.Model);
        var voice = modelVoice ?? request.Voice;
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for Soniox speech requests.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Language))
            throw new ArgumentException("Language is required for Soniox speech requests.", nameof(request));

        var format = NormalizeAudioFormat(request.OutputFormat);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["language"] = request.Language,
            ["voice"] = voice,
            ["audio_format"] = format,
            ["text"] = request.Text,
            ["sample_rate"] = metadata?.SampleRate,
            ["bitrate"] = metadata?.Bitrate,
            ["client_reference_id"] = metadata?.ClientReferenceId,
            ["speed"] = request.Speed,
            ["reduce_silence"] = metadata?.ReduceSilence
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SonioxJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Soniox TTS failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(audio)}");

        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audio),
                MimeType = ResolveAudioMimeType(format, response.Content.Headers.ContentType?.MediaType),
                Format = format
            },
            Warnings = string.IsNullOrWhiteSpace(request.Instructions)
                ? []
                : [new { type = "unsupported", feature = "instructions" }],
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Headers = response.GetHeaders()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAISpeechRequestAsync(options, cancellationToken);
        yield return new AudioSpeechStreamDelta { Audio = Convert.ToBase64String(response.Audio) };
        yield return new AudioSpeechStreamDone();
    }

    private static (string Model, string? Voice) ParseSpeechModel(string value)
    {
        var model = NormalizeSonioxModel(value);
        var slash = model.IndexOf('/');
        return slash < 0 ? (model, null) : (model[..slash], model[(slash + 1)..]);
    }

    private static string NormalizeSonioxModel(string value)
    {
        var model = value.Trim();
        const string prefix = "soniox/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? model[prefix.Length..] : model;
    }

    private static string NormalizeAudioFormat(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        null or "" => "mp3",
        "wave" => "wav",
        "pcm" => "pcm_s16le",
        var value => value
    };

    private static string ResolveAudioMimeType(string format, string? responseType)
    {
        if (!string.IsNullOrWhiteSpace(responseType) && responseType != "application/octet-stream")
            return responseType;
        return format switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm_s16le" or "pcm_s16be" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }
}
