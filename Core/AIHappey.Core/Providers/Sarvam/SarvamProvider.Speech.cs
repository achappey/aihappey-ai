using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        // Map unified outputFormat -> Sarvam output_audio_codec.
        var outputAudioCodec = NormalizeSarvamCodec(request.OutputFormat ?? ReadSpeechMetadataString(metadata, "output_audio_codec"));

        var payload = BuildSarvamSpeechPayload(request, metadata);

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
        var currentModel = await this.GetModel(request.Model, cancellationToken);

        var providerKey = GetIdentifier();

        var inputCharacters = request.Text.Length;

        decimal? cost = null;

        if (currentModel?.Pricing?.Input is not null)
        {
            cost = inputCharacters * currentModel.Pricing.Input;
        }

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = outputAudioCodec ?? "mp3"
            },
            ProviderMetadata = providerKey.CreatePrimitiveProviderMetadata(
                    costs: cost
            ),
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
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

    private Dictionary<string, object?> BuildSarvamSpeechPayload(
        SpeechRequest request,
        JsonElement metadata)
    {
        var targetLanguageCode =
            request.Language
            ?? ReadSpeechMetadataString(metadata, "language_code")
            ?? "en-IN";

        var speaker = request.Voice ?? ReadSpeechMetadataString(metadata, "speaker");
        var model = ResolveSarvamSpeechModel(request.Model, ReadSpeechMetadataString(metadata, "model"));
        var outputAudioCodec = NormalizeSarvamCodec(request.OutputFormat ?? ReadSpeechMetadataString(metadata, "output_audio_codec"));

        var payload = CopySpeechMetadata(metadata);
        RemoveSpeechMetadataProperty(payload, "language_code");
        RemoveSpeechMetadataProperty(payload, "speaker");
        RemoveSpeechMetadataProperty(payload, "pitch");
        RemoveSpeechMetadataProperty(payload, "pace");
        RemoveSpeechMetadataProperty(payload, "loudness");
        RemoveSpeechMetadataProperty(payload, "speech_sample_rate");
        RemoveSpeechMetadataProperty(payload, "enable_preprocessing");
        RemoveSpeechMetadataProperty(payload, "model");
        RemoveSpeechMetadataProperty(payload, "output_audio_codec");
        RemoveSpeechMetadataProperty(payload, "temperature");
        RemoveSpeechMetadataProperty(payload, "dict_id");
        RemoveSpeechMetadataProperty(payload, "enable_cached_responses");
        RemoveSpeechMetadataProperty(payload, "output_audio_bitrate");

        payload["text"] = request.Text;
        payload["language_code"] = targetLanguageCode;
        payload["model"] = model;

        // Request-level values continue to override their provider metadata equivalents.
        if (!string.IsNullOrWhiteSpace(speaker))
            payload["speaker"] = speaker;

        var pitch = ReadSpeechMetadataDouble(metadata, "pitch");
        if (pitch != null)
            payload["pitch"] = pitch;

        var pace = ReadSpeechMetadataDouble(metadata, "pace");
        if (pace != null || request.Speed != null)
            payload["pace"] = pace ?? request.Speed;

        var loudness = ReadSpeechMetadataDouble(metadata, "loudness");
        if (loudness != null)
            payload["loudness"] = loudness;

        var speechSampleRate = ReadSpeechMetadataInt32(metadata, "speech_sample_rate");
        if (speechSampleRate != null)
            payload["speech_sample_rate"] = speechSampleRate;

        var enablePreprocessing = ReadSpeechMetadataBoolean(metadata, "enable_preprocessing");
        if (enablePreprocessing != null)
            payload["enable_preprocessing"] = enablePreprocessing;

        if (!string.IsNullOrWhiteSpace(outputAudioCodec))
            payload["output_audio_codec"] = outputAudioCodec;

        var temperature = ReadSpeechMetadataDouble(metadata, "temperature");
        if (temperature != null)
            payload["temperature"] = temperature;

        var dictionaryId = ReadSpeechMetadataString(metadata, "dict_id");
        if (!string.IsNullOrWhiteSpace(dictionaryId))
            payload["dict_id"] = dictionaryId;

        var enableCachedResponses = ReadSpeechMetadataBoolean(metadata, "enable_cached_responses");
        if (enableCachedResponses != null)
            payload["enable_cached_responses"] = enableCachedResponses;

        var outputAudioBitrate = ReadSpeechMetadataString(metadata, "output_audio_bitrate");
        if (!string.IsNullOrWhiteSpace(outputAudioBitrate))
            payload["output_audio_bitrate"] = outputAudioBitrate;

        if (string.Equals(model, "bulbul:v3", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("pitch");
            payload.Remove("loudness");
            payload.Remove("enable_preprocessing");
        }

        return payload;
    }

    private static Dictionary<string, object?> CopySpeechMetadata(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return payload;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Sarvam provider metadata must be a JSON object.");

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static void RemoveSpeechMetadataProperty(Dictionary<string, object?> payload, string propertyName)
    {
        foreach (var name in payload.Keys
                     .Where(name => string.Equals(name, propertyName, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            payload.Remove(name);
    }

    private static string? ReadSpeechMetadataString(JsonElement metadata, string propertyName)
        => ReadSpeechMetadataValue<string>(metadata, propertyName);

    private static double? ReadSpeechMetadataDouble(JsonElement metadata, string propertyName)
        => ReadSpeechMetadataValue<double?>(metadata, propertyName);

    private static int? ReadSpeechMetadataInt32(JsonElement metadata, string propertyName)
        => ReadSpeechMetadataValue<int?>(metadata, propertyName);

    private static bool? ReadSpeechMetadataBoolean(JsonElement metadata, string propertyName)
        => ReadSpeechMetadataValue<bool?>(metadata, propertyName);

    private static T? ReadSpeechMetadataValue<T>(JsonElement metadata, string propertyName)
    {
        if (metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Sarvam provider metadata must be a JSON object.");

        JsonElement value = default;
        var found = false;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return !found || value.ValueKind == JsonValueKind.Null
            ? default
            : value.Deserialize<T>(SpeechJson);
    }

    private string ResolveSarvamSpeechModel(string requestModel, string? metadataModel)
    {
        var model = !string.IsNullOrWhiteSpace(metadataModel)
            ? metadataModel.Trim()
            : requestModel.Trim();

        var providerPrefix = GetIdentifier() + "/";
        return model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model[providerPrefix.Length..]
            : model;
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
