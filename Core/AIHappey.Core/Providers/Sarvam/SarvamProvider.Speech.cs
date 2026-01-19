using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Sarvam;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Sarvam's REST TTS does not accept these unified fields directly.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var metadata = request.GetSpeechProviderMetadata<SarvamSpeechProviderMetadata>(GetIdentifier());

        // Sarvam requires target_language_code.
        var targetLanguageCode =
            request.Language
            ?? metadata?.TargetLanguageCode
            ?? "en-IN";

        var speaker = request.Voice ?? metadata?.Speaker;

        // Map unified outputFormat -> Sarvam output_audio_codec.
        var outputAudioCodec = NormalizeSarvamCodec(request.OutputFormat ?? metadata?.OutputAudioCodec);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["target_language_code"] = targetLanguageCode
        };

        // optional Sarvam inputs
        if (!string.IsNullOrWhiteSpace(speaker))
            payload["speaker"] = speaker;

        if (metadata?.Pitch != null)
            payload["pitch"] = metadata.Pitch;

        if (metadata?.Pace != null)
            payload["pace"] = metadata.Pace;

        if (metadata?.Loudness != null)
            payload["loudness"] = metadata.Loudness;

        if (metadata?.SpeechSampleRate != null)
            payload["speech_sample_rate"] = metadata.SpeechSampleRate;

        if (metadata?.EnablePreprocessing != null)
            payload["enable_preprocessing"] = metadata.EnablePreprocessing;

        if (!string.IsNullOrWhiteSpace(metadata?.Model))
            payload["model"] = metadata.Model;

        if (!string.IsNullOrWhiteSpace(outputAudioCodec))
            payload["output_audio_codec"] = outputAudioCodec;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text-to-speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam TTS failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<SarvamTtsResponse>(body, SpeechJson);
        var audioBase64 = parsed?.Audios?.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"Sarvam TTS returned no audio. Body: {body}");

        // Sarvam docs: output audio is a wave file encoded as base64 string.
        // If output_audio_codec is set, best-effort mime mapping.
        var mime = GuessMimeType(outputAudioCodec) ?? "audio/wav";

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = outputAudioCodec ?? "mp3"
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = parsed
            }
        };
    }

    private static string? NormalizeSarvamCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;

        var c = codec.Trim().ToLowerInvariant();

        // Accept common aliases.
        if (c is "wave")
            c = "wav";

        // Keep only known values to avoid Sarvam 422s on typos.
        return c switch
        {
            "mp3" or "linear16" or "mulaw" or "alaw" or "opus" or "flac" or "aac" or "wav" => c,
            _ => codec.Trim() // pass-through unknowns to let Sarvam validate (and surface their error)
        };
    }

    private static string? GuessMimeType(string? codec)
    {
        var c = codec?.Trim().ToLowerInvariant();
        return c switch
        {
            null or "" => null,
            "wav" or "linear16" => "audio/wav",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            // μ-law / A-law are telephony PCM variants; treat as best-effort.
            "mulaw" or "alaw" => "audio/basic",
            _ => null
        };
    }

    private sealed class SarvamTtsResponse
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("audios")]
        public List<string>? Audios { get; set; }
    }
}

