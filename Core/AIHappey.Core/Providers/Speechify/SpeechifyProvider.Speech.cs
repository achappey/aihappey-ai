using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Speechify;

public partial class SpeechifyProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => SpeechRequest(request, null, cancellationToken);

    private async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties,
        CancellationToken cancellationToken)
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

        var metadataElement = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        Dictionary<string, JsonElement> metadata = metadataElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? []
            : metadataElement.Deserialize<Dictionary<string, JsonElement>>(JsonSerializerOptions.Web)
                ?? throw new JsonException("Speechify provider options must be a JSON object.");
        var metadataVoiceId = GetMetadataValue<string>(metadata, "voice_id");
        var metadataAudioFormat = GetMetadataValue<string>(metadata, "audio_format");
        var metadataLanguage = GetMetadataValue<string>(metadata, "language");
        var options = PrepareOptions(metadata);
        RemoveMetadataProperties(metadata, "voice_id", "audio_format", "language", "options");

        var (model, modelVoiceId) = ResolveModelAndVoice(request.Model);

        // ---- resolve required voice_id ----
        // Contract requested by user:
        // 1) Speechify voice shortcut in the model id
        // 2) providerOptions.speechify.voice_id
        // 3) SpeechRequest.voice
        // 4) error
        var voiceId = (modelVoiceId ?? metadataVoiceId ?? request.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Speechify requires a voice_id. Provide a Speechify voice shortcut in the model id, providerOptions.speechify.voice_id, or SpeechRequest.voice.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoiceId, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(metadataVoiceId)
            && !string.Equals(modelVoiceId, metadataVoiceId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice_id", reason = "voice_id is derived from model id" });
        }

        // Speechify audio_format: recommend always passing it.
        var audioFormat = NormalizeSpeechifyAudioFormat(
            request.OutputFormat
            ?? metadataAudioFormat
            ?? "wav");

        var language = (request.Language ?? metadataLanguage)?.Trim();

        var payload = metadata.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        if (additionalProperties is not null)
        {
            foreach (var (key, value) in additionalProperties)
                payload[key] = value.Clone();
        }

        payload["input"] = request.Text;
        payload["voice_id"] = voiceId;
        payload["model"] = model;

        // optional top-level fields
        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (!string.IsNullOrWhiteSpace(audioFormat))
            payload["audio_format"] = audioFormat;

        if (options is not null)
            payload["options"] = JsonSerializer.SerializeToElement(options);

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
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
             Response = new ResponseData
            {
                Timestamp = now,
                ModelId = BuildResolvedSpeechifyModelId(model, modelVoiceId),
                Body = root.Clone()
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static JsonObject? PrepareOptions(Dictionary<string, JsonElement> metadata)
    {
        var optionsKey = FindMetadataKey(metadata, "options");
        if (optionsKey is null)
            return null;

        var element = metadata[optionsKey];
        metadata.Remove(optionsKey);
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("Speechify provider options 'options' must be a JSON object.");

        var options = JsonNode.Parse(element.GetRawText())!.AsObject();
        foreach (var propertyName in new[] { "loudness_normalization", "text_normalization" })
        {
            var optionKey = options.Select(property => property.Key)
                .FirstOrDefault(key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));
            if (optionKey is null)
                continue;

            var value = options[optionKey];
            if (value is null)
                options.Remove(optionKey);
            else
                _ = value.Deserialize<bool?>(SpeechJson);
        }

        return options.Count == 0 ? null : options;
    }

    private static T? GetMetadataValue<T>(
        IReadOnlyDictionary<string, JsonElement> metadata,
        string propertyName)
    {
        var key = FindMetadataKey(metadata, propertyName);
        return key is null
            ? default
            : metadata[key].Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static string? FindMetadataKey(
        IReadOnlyDictionary<string, JsonElement> metadata,
        string propertyName)
        => metadata.Keys.FirstOrDefault(key =>
            string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase));

    private static void RemoveMetadataProperties(
        IDictionary<string, JsonElement> metadata,
        params string[] propertyNames)
    {
        foreach (var key in metadata.Keys
                     .Where(key => propertyNames.Any(propertyName =>
                         string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
        {
            metadata.Remove(key);
        }
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

    private (string Model, string? VoiceId) ResolveModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && !string.IsNullOrWhiteSpace(parts[0]))
            return (parts[0], null);

        if (parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1]))
            return (parts[0], parts[1]);

        throw new ArgumentException("Speechify model must be '{model}' or '{model}/{voiceId}'.", nameof(model));
    }

    private string BuildResolvedSpeechifyModelId(string model, string? voiceId)
        => string.IsNullOrWhiteSpace(voiceId)
            ? model.ToModelId(GetIdentifier())
            : $"{model}/{voiceId}".ToModelId(GetIdentifier());
}
