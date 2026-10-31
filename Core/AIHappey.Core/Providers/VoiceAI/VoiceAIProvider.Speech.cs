using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VoiceAI;

public partial class VoiceAIProvider
{
    private static readonly JsonSerializerOptions SpeechJsonOptions = new(JsonSerializerDefaults.Web)
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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var voiceOption = ReadStringOption(metadata, "voice_id");
        var languageOption = ReadStringOption(metadata, "language");
        var audioFormatOption = ReadStringOption(metadata, "audio_format");
        var temperatureOption = ReadSingleOption(metadata, "temperature");
        var topPOption = ReadSingleOption(metadata, "top_p");
        var (baseModelId, modelVoiceId, modelLanguage) = ParseModelVoiceAndLanguage(request.Model);

        var voiceId = (modelVoiceId ?? request.Voice ?? voiceOption)?.Trim();
        var language = (modelLanguage ?? request.Language ?? languageOption)?.Trim()?.ToLowerInvariant();
        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? audioFormatOption) ?? "mp3";
        var temperature = temperatureOption;
        var topP = topPOption;

        if (!BaseSpeechModels.Contains(baseModelId, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} speech model '{request.Model}' is not supported.");

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(voiceOption)
                && !string.Equals(voiceOption.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.voiceai.voice_id", reason = "voice is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(modelLanguage))
        {
            if (!string.IsNullOrWhiteSpace(request.Language)
                && !string.Equals(request.Language.Trim(), modelLanguage, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(languageOption)
                && !string.Equals(languageOption.Trim(), modelLanguage, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.voiceai.language", reason = "language is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyAdditionalOptions(metadata, payload);
        SetPayloadValue(payload, "text", request.Text);
        SetPayloadValue(payload, "model", baseModelId);
        SetPayloadValue(payload, "audio_format", outputFormat);

        if (!string.IsNullOrWhiteSpace(voiceId))
            SetPayloadValue(payload, "voice_id", voiceId);
        else
            RemovePayloadValue(payload, "voice_id");

        if (!string.IsNullOrWhiteSpace(language))
            SetPayloadValue(payload, "language", language);
        else
            RemovePayloadValue(payload, "language");

        if (temperature is not null)
            SetPayloadValue(payload, "temperature", temperature.Value);
        else
            RemovePayloadValue(payload, "temperature");

        if (topP is not null)
            SetPayloadValue(payload, "top_p", topP.Value);
        else
            RemovePayloadValue(payload, "top_p");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/tts/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(outputFormat, mediaType),
                Format = outputFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static (string BaseModelId, string? VoiceId, string? Language) ParseModelVoiceAndLanguage(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderId + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => (parts[0], null, null),
            3 => (parts[0], parts[1], parts[2].ToLowerInvariant()),
            _ => throw new ArgumentException("VoiceAI speech model must be either '{baseModel}' or '{baseModel}/{voiceId}/{language}'.", nameof(model))
        };
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            var fmt => fmt
        };
    }

    private static void CopyAdditionalOptions(JsonElement metadata, Dictionary<string, object?> payload)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return;

        EnsureMetadataObject(metadata);
        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private static string? ReadStringOption(JsonElement metadata, string name)
        => ReadMappedReferenceOption<string>(metadata, name, value => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new JsonException($"The VoiceAI provider option '{name}' must be a string or null.")
        });

    private static float? ReadSingleOption(JsonElement metadata, string name)
        => ReadMappedValueOption<float>(metadata, name, value =>
        {
            if (value.ValueKind == JsonValueKind.Null)
                return null;

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number))
                throw new JsonException($"The VoiceAI provider option '{name}' must be a number or null.");

            return number;
        });

    private static T? ReadMappedReferenceOption<T>(JsonElement metadata, string name, Func<JsonElement, T?> read)
        where T : class
    {
        var value = FindMappedOption(metadata, name);
        return value is { } found ? read(found) : default;
    }

    private static T? ReadMappedValueOption<T>(JsonElement metadata, string name, Func<JsonElement, T?> read)
        where T : struct
    {
        var value = FindMappedOption(metadata, name);
        return value is { } found ? read(found) : default;
    }

    private static JsonElement? FindMappedOption(JsonElement metadata, string name)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return null;

        EnsureMetadataObject(metadata);
        JsonElement? found = null;
        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                found = property.Value;
        }

        return found;
    }

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("VoiceAI providerOptions must be a JSON object.");
    }

    private static void SetPayloadValue(Dictionary<string, object?> payload, string name, object value)
    {
        RemovePayloadValue(payload, name);
        payload[name] = value;
    }

    private static void RemovePayloadValue(Dictionary<string, object?> payload, string name)
    {
        foreach (var key in payload.Keys.Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToList())
            payload.Remove(key);
    }

    private static string ResolveSpeechMimeType(string outputFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType!;

        return outputFormat.ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
    }
}
