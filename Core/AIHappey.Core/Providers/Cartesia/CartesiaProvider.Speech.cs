using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
    public Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
        => SpeechRequest(request, additionalProperties: null, cancellationToken);

    private async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        IReadOnlyDictionary<string, JsonElement>? additionalProperties,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        ValidateMetadataObject(metadata);
        var apiVersion = GetStringOption(metadata, "apiVersion");
        var metadataLanguage = GetStringOption(metadata, "language");
        var metadataContainer = GetStringOption(metadata, "container");
        var sampleRate = GetIntOption(metadata, "sampleRate");
        var bitRate = GetIntOption(metadata, "bitRate");
        var encoding = GetStringOption(metadata, "encoding");
        var metadataSpeed = GetFloatOption(metadata, "speed");
        var volume = GetFloatOption(metadata, "volume");
        var emotion = GetStringOption(metadata, "emotion");

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var normalizedModel = request.Model;
        var (ttsModelId, voiceId) = ParseTtsModelAndVoiceFromModel(normalizedModel);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var language = (request.Language ?? metadataLanguage)?.Trim();
        var container = NormalizeContainer(request.OutputFormat ?? metadataContainer) ?? "wav";

        var speed = request.Speed ?? metadataSpeed;
        if (speed is { } s && (s < 0.6f || s > 1.5f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Cartesia speed must be between 0.6 and 1.5.");

        if (volume is { } volumeValue && (volumeValue < 0.5f || volumeValue > 2.0f))
            throw new ArgumentOutOfRangeException("Volume", "Cartesia volume must be between 0.5 and 2.0.");

        var isSonic3 = ttsModelId.StartsWith("sonic-3", StringComparison.OrdinalIgnoreCase);
        if (!isSonic3)
        {
            if (speed is not null)
                warnings.Add(new { type = "ignored", feature = "speed", reason = "generation_config.speed only affects sonic-3 models" });
            if (volume is not null)
                warnings.Add(new { type = "ignored", feature = "volume", reason = "generation_config.volume only affects sonic-3 models" });
            if (!string.IsNullOrWhiteSpace(emotion))
                warnings.Add(new { type = "ignored", feature = "emotion", reason = "generation_config.emotion only affects sonic-3 models" });
        }

        var payload = CopyAdditionalOptions(metadata);
        if (additionalProperties is not null)
        {
            foreach (var (name, value) in additionalProperties)
                payload[name] = value;
        }

        payload["model_id"] = ttsModelId;
        payload["transcript"] = request.Text;
        SetMappedObject(payload, "voice", new Dictionary<string, object?>
        {
            ["mode"] = "id",
            ["id"] = voiceId
        });
        SetMappedObject(payload, "output_format", BuildOutputFormat(container, sampleRate, bitRate, encoding));

        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (isSonic3)
        {
            var generationConfig = new Dictionary<string, object?>();
            if (speed is not null)
                generationConfig["speed"] = speed.Value;
            if (volume is not null)
                generationConfig["volume"] = volume.Value;
            if (!string.IsNullOrWhiteSpace(emotion))
                generationConfig["emotion"] = emotion.Trim();

            if (generationConfig.Count > 0)
                SetMappedObject(payload, "generation_config", generationConfig);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/bytes")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        ApplyVersionHeader(httpRequest, apiVersion);

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = ResolveSpeechMimeType(container, resp.Content.Headers.ContentType?.MediaType);
        var format = ResolveSpeechFormat(container, mime);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    private static (string TtsModelId, string VoiceId) ParseTtsModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(CartesiaTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{CartesiaTtsModelPrefix}[ttsModelId]/[voiceId]'.");

        var tail = model[CartesiaTtsModelPrefix.Length..].Trim();
        var slashIndex = tail.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both tts model id and voice id after 'tts/'.", nameof(model));

        var ttsModelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(ttsModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both tts model id and voice id after 'tts/'.", nameof(model));

        if (!SupportedTtsModelIds.Any(m => string.Equals(m, ttsModelId, StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException($"{ProviderName} TTS model '{ttsModelId}' is not supported.");

        return (ttsModelId, voiceId);
    }

    private static Dictionary<string, object?> BuildOutputFormat(
        string container,
        int? sampleRate,
        int? bitRate,
        string? encoding)
    {
        var normalized = NormalizeContainer(container) ?? "wav";

        if (string.Equals(normalized, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["container"] = "mp3",
                ["sample_rate"] = sampleRate ?? 44100,
                ["bit_rate"] = bitRate ?? 128000
            };
        }

        if (string.Equals(normalized, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["container"] = "raw",
                ["encoding"] = NormalizeEncoding(encoding) ?? "pcm_s16le",
                ["sample_rate"] = sampleRate ?? 44100
            };
        }

        return new Dictionary<string, object?>
        {
            ["container"] = "wav",
            ["encoding"] = NormalizeEncoding(encoding) ?? "pcm_s16le",
            ["sample_rate"] = sampleRate ?? 44100
        };
    }

    private static Dictionary<string, object?> CopyAdditionalOptions(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return payload;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Cartesia providerOptions must be a JSON object.");

        foreach (var property in metadata.EnumerateObject())
        {
            if (IsMappedOption(property.Name))
                continue;

            payload[property.Name] = property.Value;
        }

        return payload;
    }

    private static void ValidateMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined))
            throw new JsonException("Cartesia providerOptions must be a JSON object.");
    }

    private static bool IsMappedOption(string name)
        => name.Equals("apiVersion", StringComparison.OrdinalIgnoreCase)
            || name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("container", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sampleRate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bitRate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("speed", StringComparison.OrdinalIgnoreCase)
            || name.Equals("volume", StringComparison.OrdinalIgnoreCase)
            || name.Equals("emotion", StringComparison.OrdinalIgnoreCase);

    private static void SetMappedObject(
        Dictionary<string, object?> payload,
        string name,
        Dictionary<string, object?> mappedValues)
    {
        if (payload.TryGetValue(name, out var additionalValue)
            && additionalValue is JsonElement { ValueKind: JsonValueKind.Object } additionalObject)
        {
            foreach (var property in additionalObject.EnumerateObject())
                mappedValues.TryAdd(property.Name, property.Value);
        }

        payload[name] = mappedValues;
    }

    private static string? GetStringOption(JsonElement metadata, string name)
        => GetOption<string>(metadata, name);

    private static int? GetIntOption(JsonElement metadata, string name)
        => GetOption<int?>(metadata, name);

    private static float? GetFloatOption(JsonElement metadata, string name)
        => GetOption<float?>(metadata, name);

    private static T? GetOption<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            JsonElement? option = null;
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    option = property.Value;
            }

            return option.HasValue
                ? option.Value.Deserialize<T>(JsonSerializerOptions.Web)
                : default;
        }

        return default;
    }

    private static string? NormalizeContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
            return null;

        var normalized = container.Trim().ToLowerInvariant();

        return normalized switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            "pcm" => "raw",
            "mp3" or "raw" or "wav" => normalized,
            _ => null
        };
    }

    private static string? NormalizeEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
            return null;

        return encoding.Trim().ToLowerInvariant();
    }

    private static string ResolveSpeechMimeType(string container, string? responseContentType)
    {
        if (!string.IsNullOrWhiteSpace(responseContentType))
            return responseContentType;

        return NormalizeContainer(container) switch
        {
            "mp3" => "audio/mpeg",
            "raw" => "audio/pcm",
            _ => "audio/wav"
        };
    }

    private static string ResolveSpeechFormat(string container, string mime)
    {
        var normalized = NormalizeContainer(container);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (mime.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (mime.Contains("wav", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (mime.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "raw";
        return "wav";
    }
}
