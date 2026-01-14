using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Speechify;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Speechify;

public partial class SpeechifyProvider
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

        // Speechify supports emotion/pitch/speed through SSML prosody per docs; our unified knobs
        // do not map 1:1 to their API request shape.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var metadata = request.GetSpeechProviderMetadata<SpeechifySpeechProviderMetadata>(GetIdentifier());

        // ---- resolve required voice_id ----
        // Contract requested by user:
        // 1) providerOptions.speechify.voice_id
        // 2) SpeechRequest.voice
        // 3) error
        var voiceId = (metadata?.VoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Speechify requires a voice_id. Provide providerOptions.speechify.voice_id or SpeechRequest.voice.", nameof(request));

        // Speechify audio_format: recommend always passing it.
        var audioFormat = NormalizeSpeechifyAudioFormat(
            request.OutputFormat
            ?? metadata?.AudioFormat
            ?? "wav");

        var language = (request.Language ?? metadata?.Language)?.Trim();

        // Allow providerOptions override of model if desired.
        var model = request.Model.Trim();

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text,
            ["voice_id"] = voiceId,
            ["model"] = model,
        };

        // optional top-level fields
        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (!string.IsNullOrWhiteSpace(audioFormat))
            payload["audio_format"] = audioFormat;

        // ---- options (Speechify wrapper object) ----
        if (metadata?.Options is not null)
        {
            var options = new Dictionary<string, object?>();

            if (metadata.Options.LoudnessNormalization is not null)
                options["loudness_normalization"] = metadata.Options.LoudnessNormalization;

            if (metadata.Options.TextNormalization is not null)
                options["text_normalization"] = metadata.Options.TextNormalization;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechify TTS failed ({(int)resp.StatusCode}): {body}");

        // Response shape:
        // {
        //   "audio_data": "<base64>",
        //   "audio_format": "wav",
        //   "billable_characters_count": 123,
        //   ...
        // }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioBase64 = root.TryGetProperty("audio_data", out var ad) && ad.ValueKind == JsonValueKind.String
            ? (ad.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"Speechify TTS returned no audio_data. Body: {body}");

        var returnedFormat = root.TryGetProperty("audio_format", out var af) && af.ValueKind == JsonValueKind.String
            ? af.GetString()
            : null;

        var effectiveFormat = NormalizeSpeechifyAudioFormat(returnedFormat ?? audioFormat) ?? "wav";
        var mime = MapSpeechifyFormatToMimeType(effectiveFormat);
        var audioDataUrl = audioBase64.ToDataUrl(mime);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse()
            {
                Base64 = audioDataUrl,
                MimeType = mime,
                Format = effectiveFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string? NormalizeSpeechifyAudioFormat(string? audioFormat)
    {
        if (string.IsNullOrWhiteSpace(audioFormat))
            return null;

        var fmt = audioFormat.Trim().ToLowerInvariant();

        // accept common aliases
        if (fmt is "mpeg") fmt = "mp3";
        if (fmt is "wave") fmt = "wav";

        return fmt switch
        {
            "wav" => "wav",
            "mp3" => "mp3",
            "ogg" => "ogg",
            "aac" => "aac",
            "pcm" => "pcm",
            _ => fmt // pass-through unknowns so Speechify can validate
        };
    }

    private static string MapSpeechifyFormatToMimeType(string audioFormat)
    {
        var fmt = (audioFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "aac" => "audio/aac",
            // PCM can be raw; best-effort exposure.
            "pcm" => "audio/L16",
            _ => "application/octet-stream"
        };
    }
}

