using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.MurfAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
{
    private static readonly JsonSerializerOptions MurfJson = new(JsonSerializerDefaults.Web)
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

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Murf doesn't support these as a unified surface in the /v1/speech/generate shape.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetProviderMetadata<MurfAISpeechProviderMetadata>(GetIdentifier());

        // ---- required: voiceId ----
        var voiceId = (metadata?.VoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("MurfAI requires a voiceId. Provide providerOptions.murfai.voiceId or SpeechRequest.voice.", nameof(request));

        // Murf defaults to GEN2; our default model is murfai/gen2.
        var modelVersion = (metadata?.ModelVersion ?? "GEN2").Trim();

        // Output format: default WAV (Murf default).
        var format = NormalizeMurfFormat(request.OutputFormat ?? metadata?.Format) ?? "wav";

        // Default encodeAsBase64=true to keep output consistent and enable zero retention.
        var encodeAsBase64 = metadata?.EncodeAsBase64 ?? true;

        // Language → multiNativeLocale (only if not explicitly set in providerOptions)
        var multiNativeLocale = !string.IsNullOrWhiteSpace(metadata?.MultiNativeLocale)
            ? metadata!.MultiNativeLocale
            : (!string.IsNullOrWhiteSpace(request.Language) ? request.Language!.Trim() : null);

        // Unified speed (float, typically ~0.25..4) → Murf rate (-50..50)
        // Mapping contract: rate = clamp(round((speed - 1.0) * 50)).
        int? rate = metadata?.Rate;
        if (request.Speed is not null)
        {
            rate = (int)Math.Round((request.Speed.Value - 1.0f) * 50.0f, MidpointRounding.AwayFromZero);
            rate = Math.Clamp(rate.Value, -50, 50);
        }

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voiceId"] = voiceId,
            ["modelVersion"] = modelVersion,
            ["format"] = format.ToUpperInvariant(),
            ["encodeAsBase64"] = encodeAsBase64,
        };

        if (metadata?.AudioDuration is not null && metadata.AudioDuration.Value > 0)
            payload["audioDuration"] = metadata.AudioDuration.Value;

        if (!string.IsNullOrWhiteSpace(metadata?.ChannelType))
            payload["channelType"] = metadata!.ChannelType;

        if (!string.IsNullOrWhiteSpace(multiNativeLocale))
            payload["multiNativeLocale"] = multiNativeLocale;

        if (metadata?.Pitch is not null)
            payload["pitch"] = Math.Clamp(metadata.Pitch.Value, -50, 50);

        if (rate is not null)
            payload["rate"] = Math.Clamp(rate.Value, -50, 50);

        if (metadata?.SampleRate is not null)
            payload["sampleRate"] = metadata.SampleRate.Value;

        if (!string.IsNullOrWhiteSpace(metadata?.Style))
            payload["style"] = metadata!.Style;

        if (metadata?.Variation is not null)
            payload["variation"] = Math.Clamp(metadata.Variation.Value, 0, 5);

        if (metadata?.WordDurationsAsOriginalText is not null)
            payload["wordDurationsAsOriginalText"] = metadata.WordDurationsAsOriginalText.Value;

        if (metadata?.PronunciationDictionary is not null)
            payload["pronunciationDictionary"] = metadata.PronunciationDictionary;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/speech/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, MurfJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MurfAI TTS failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Prefer encodedAudio for zero-retention mode.
        var encodedAudio = root.TryGetProperty("encodedAudio", out var ea) && ea.ValueKind == JsonValueKind.String
            ? (ea.GetString() ?? string.Empty)
            : string.Empty;

        string audioBase64;
        if (!string.IsNullOrWhiteSpace(encodedAudio))
        {
            audioBase64 = encodedAudio;
        }
        else
        {
            // Fallback: fetch audioFile URL and base64-encode.
            var audioFile = root.TryGetProperty("audioFile", out var af) && af.ValueKind == JsonValueKind.String
                ? af.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(audioFile))
                throw new InvalidOperationException($"MurfAI TTS returned neither encodedAudio nor audioFile. Body: {body}");

            var bytes = await _client.GetByteArrayAsync(audioFile, cancellationToken);
            audioBase64 = Convert.ToBase64String(bytes);
        }

        var mime = MapMurfFormatToMimeType(format);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = format
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

    private static string? NormalizeMurfFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var f = format.Trim().ToLowerInvariant();
        if (f is "mpeg") f = "mp3";
        if (f is "wave") f = "wav";

        // Murf accepts these (per docs): MP3, WAV, FLAC, ALAW, ULAW, PCM, OGG
        return f switch
        {
            "mp3" => "mp3",
            "wav" => "wav",
            "flac" => "flac",
            "alaw" => "alaw",
            "ulaw" => "ulaw",
            "pcm" => "pcm",
            "ogg" => "ogg",
            _ => f // pass-through unknowns so Murf can validate and return a helpful error
        };
    }

    private static string MapMurfFormatToMimeType(string? format)
    {
        var f = (format ?? string.Empty).Trim().ToLowerInvariant();
        return f switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            // ALAW/ULAW/PCM are often raw; best-effort exposure.
            "alaw" or "ulaw" or "pcm" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }
}

