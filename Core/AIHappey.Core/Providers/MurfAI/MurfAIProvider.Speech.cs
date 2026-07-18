using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.MurfAI;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
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

        var (modelVersionFromModel, voiceIdFromModel) = ParseSpeechModelAndVoice(request.Model);

        // A voice selected from the model catalogue is authoritative. The legacy explicit
        // voice fields remain supported for base Murf model identifiers.
        var voiceId = (voiceIdFromModel ?? metadata?.VoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("MurfAI requires a voiceId. Provide a MurfAI voice shortcut model, providerOptions.murfai.voiceId, or SpeechRequest.voice.", nameof(request));

        if (!string.IsNullOrWhiteSpace(voiceIdFromModel))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), voiceIdFromModel, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId)
                && !string.Equals(metadata.VoiceId.Trim(), voiceIdFromModel, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.murfai.voiceId", reason = "voice is derived from model id" });
            }
        }

        // Murf defaults to GEN2; catalogue model identifiers additionally select GEN2 or Falcon 2.
        var modelVersion = (modelVersionFromModel ?? metadata?.ModelVersion ?? "GEN2").Trim();

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
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
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

    private static (string? ModelVersion, string? VoiceId) ParseSpeechModelAndVoice(string model)
    {
        var localModel = model.Trim();
        var providerPrefix = "murfai/";
        if (localModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            localModel = localModel[providerPrefix.Length..];

        var parts = localModel.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || string.IsNullOrWhiteSpace(parts[0]))
            throw new ArgumentException("MurfAI speech model must be 'gen2', 'falcon-2', 'gen2/{voiceId}', or 'falcon-2/{voiceId}'.", nameof(model));

        if (!MurfSpeechModelVersions.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            return (null, null);

        if (parts.Length == 1)
            return (parts[0], null);

        if (string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("MurfAI speech model shortcut must include a voice id after the model version.", nameof(model));

        return (parts[0], parts[1]);
    }
}

