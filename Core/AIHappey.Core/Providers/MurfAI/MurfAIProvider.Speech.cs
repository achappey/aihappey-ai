using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyProviderOptions(metadata);
        var metadataVoiceId = GetOption<string?>(metadata, "voiceId");
        var metadataModelVersion = GetOption<string?>(metadata, "modelVersion");
        var metadataFormat = GetOption<string?>(metadata, "format");
        var encodeAsBase64Option = GetOption<bool?>(metadata, "encodeAsBase64");
        var metadataLocale = GetOption<string?>(metadata, "multiNativeLocale");
        var metadataRate = GetOption<int?>(metadata, "rate");
        var audioDuration = GetOption<double?>(metadata, "audioDuration");
        var channelType = GetOption<string?>(metadata, "channelType");
        var pitch = GetOption<int?>(metadata, "pitch");
        var sampleRate = GetOption<double?>(metadata, "sampleRate");
        var style = GetOption<string?>(metadata, "style");
        var variation = GetOption<int?>(metadata, "variation");
        var wordDurationsAsOriginalText = GetOption<bool?>(metadata, "wordDurationsAsOriginalText");

        var (modelVersionFromModel, voiceIdFromModel) = ParseSpeechModelAndVoice(request.Model);

        // A voice selected from the model catalogue is authoritative. The legacy explicit
        // voice fields remain supported for base Murf model identifiers.
        var voiceId = (voiceIdFromModel ?? metadataVoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("MurfAI requires a voiceId. Provide a MurfAI voice shortcut model, providerOptions.murfai.voiceId, or SpeechRequest.voice.", nameof(request));

        if (!string.IsNullOrWhiteSpace(voiceIdFromModel))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), voiceIdFromModel, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadataVoiceId)
                && !string.Equals(metadataVoiceId.Trim(), voiceIdFromModel, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.murfai.voiceId", reason = "voice is derived from model id" });
            }
        }

        // Murf defaults to GEN2; catalogue model identifiers additionally select GEN2 or Falcon 2.
        var modelVersion = (modelVersionFromModel ?? metadataModelVersion ?? "GEN2").Trim();

        // Output format: default WAV (Murf default).
        var format = NormalizeMurfFormat(request.OutputFormat ?? metadataFormat) ?? "wav";

        // Default encodeAsBase64=true to keep output consistent and enable zero retention.
        var encodeAsBase64 = encodeAsBase64Option ?? true;

        // Language → multiNativeLocale (only if not explicitly set in providerOptions)
        var multiNativeLocale = !string.IsNullOrWhiteSpace(metadataLocale)
            ? metadataLocale
            : (!string.IsNullOrWhiteSpace(request.Language) ? request.Language!.Trim() : null);

        // Unified speed (float, typically ~0.25..4) → Murf rate (-50..50)
        // Mapping contract: rate = clamp(round((speed - 1.0) * 50)).
        int? rate = metadataRate;
        if (request.Speed is not null)
        {
            rate = (int)Math.Round((request.Speed.Value - 1.0f) * 50.0f, MidpointRounding.AwayFromZero);
            rate = Math.Clamp(rate.Value, -50, 50);
        }

        SetOption(payload, "text", request.Text);
        SetOption(payload, "voiceId", voiceId);
        SetOption(payload, "modelVersion", modelVersion);
        SetOption(payload, "format", format.ToUpperInvariant());
        SetOption(payload, "encodeAsBase64", encodeAsBase64);
        SetOption(payload, "audioDuration", audioDuration is > 0 ? audioDuration.Value : null);
        SetOption(payload, "channelType", string.IsNullOrWhiteSpace(channelType) ? null : channelType);
        SetOption(payload, "multiNativeLocale", string.IsNullOrWhiteSpace(multiNativeLocale) ? null : multiNativeLocale);
        SetOption(payload, "pitch", pitch is null ? null : Math.Clamp(pitch.Value, -50, 50));
        SetOption(payload, "rate", rate is null ? null : Math.Clamp(rate.Value, -50, 50));
        SetOption(payload, "sampleRate", sampleRate);
        SetOption(payload, "style", string.IsNullOrWhiteSpace(style) ? null : style);
        SetOption(payload, "variation", variation is null ? null : Math.Clamp(variation.Value, 0, 5));
        SetOption(payload, "wordDurationsAsOriginalText", wordDurationsAsOriginalText);

        var pronunciationDictionary = GetOption<JsonElement>(metadata, "pronunciationDictionary");
        if (pronunciationDictionary.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            _ = pronunciationDictionary.Deserialize<Dictionary<string, JsonElement>>(MurfJson);
        SetOption(payload, "pronunciationDictionary",
            pronunciationDictionary.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? null
                : pronunciationDictionary);

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

    private static Dictionary<string, object?> CopyProviderOptions(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return payload;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("MurfAI provider options must be an object.");

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static T? GetOption<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

        JsonElement value = default;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                value = property.Value;
        }

        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        return JsonSerializer.Deserialize<T>(value.GetRawText(), MurfJson);
    }

    private static void SetOption(Dictionary<string, object?> payload, string name, object? value)
    {
        foreach (var key in payload.Keys
                     .Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            payload.Remove(key);
        }

        if (value is not null)
            payload[name] = value;
    }
}
