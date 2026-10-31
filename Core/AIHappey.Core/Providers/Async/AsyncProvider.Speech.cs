using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            && metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("providerOptions.async must be an object.");

        var voiceOptions = GetObjectProperty(metadata, "voice");
        _ = GetStringProperty(voiceOptions, "mode");
        var metadataVoiceId = GetStringProperty(voiceOptions, "id");
        var outputFormatOptions = GetObjectProperty(metadata, "output_format");
        var metadataContainer = GetStringProperty(outputFormatOptions, "container");
        var metadataSampleRate = GetInt32Property(outputFormatOptions, "sample_rate");
        var metadataBitRate = GetInt32Property(outputFormatOptions, "bit_rate");
        var metadataEncoding = GetStringProperty(outputFormatOptions, "encoding");
        var metadataLanguage = GetStringProperty(metadata, "language");
        var metadataSpeedControl = GetDoubleProperty(metadata, "speed_control");
        var metadataStability = GetInt32Property(metadata, "stability");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var (modelId, modelVoiceId) = ParseAsyncSpeechModelAndVoice(request.Model);
        var voiceId = modelVoiceId ?? request.Voice?.Trim() ?? metadataVoiceId?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("'voice' (async voice UUID) is required.");

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoiceId, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var container = (request.OutputFormat ?? metadataContainer ?? "mp3").Trim().ToLowerInvariant();
        container = container is "mp3" or "wav" or "raw" ? container : "mp3";

        var sampleRate = metadataSampleRate ?? 44100;
        var bitRate = metadataBitRate ?? 192000;
        var encoding = metadataEncoding ?? "pcm_s16le";

        var outputFormat = CopyObjectProperties(outputFormatOptions, "container", "sample_rate", "bit_rate", "encoding");
        outputFormat["container"] = container;
        outputFormat["sample_rate"] = sampleRate;

        if (container == "mp3")
        {
            outputFormat["bit_rate"] = bitRate;
            outputFormat.Remove("encoding");
        }
        else
        {
            outputFormat["encoding"] = encoding;
            outputFormat.Remove("bit_rate");
        }

        var language = request.Language ?? metadataLanguage;

        var speedControl = metadataSpeedControl;
        if (request.Speed is not null)
            speedControl = request.Speed.Value;

        var voice = CopyObjectProperties(voiceOptions, "mode", "id");
        voice["mode"] = "id";
        voice["id"] = voiceId;

        var body = CopyObjectProperties(metadata,
            "voice", "output_format", "language", "speed_control", "stability", "model_id", "transcript");
        body["model_id"] = modelId;
        body["transcript"] = request.Text;
        body["voice"] = voice;
        body["output_format"] = outputFormat;
        body["language"] = string.IsNullOrWhiteSpace(language) ? null : language;

        if (speedControl is not null)
            body["speed_control"] = speedControl;
        else
            body.Remove("speed_control");

        if (metadataStability is not null)
            body["stability"] = metadataStability;
        else
            body.Remove("stability");

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var resp = await _client.PostAsync("text_to_speech", content, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"asyncAI TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var mime = container switch
        {
            "wav" => "audio/wav",
            "raw" => "application/octet-stream",
            _ => "audio/mpeg"
        };

        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audio,
                MimeType = mime,
                Format = container ?? "mp3"
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = modelId,
                    voice = voiceId
                }, JsonSerializerOptions.Web)
            },
            Request = new()
            {
                Body = body
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = $"{modelId}/{voiceId}".ToModelId(GetIdentifier()),
            }
        };
    }

    private string NormalizeAsyncModelId(string model)
    {
        var normalized = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..].Trim();

        return normalized;
    }

    private (string ModelId, string? VoiceId) ParseAsyncSpeechModelAndVoice(string model)
    {
        var normalized = NormalizeAsyncModelId(model);

        foreach (var speechModel in AsyncSpeechModelIds)
        {
            if (string.Equals(normalized, speechModel, StringComparison.OrdinalIgnoreCase))
                return (speechModel, null);

            var prefix = speechModel + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var voiceId = normalized[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(voiceId))
                throw new ArgumentException($"Async speech shortcut model must be in the form '{{model}}/{{voice_id}}'.", nameof(model));

            return (speechModel, voiceId);
        }

        throw new NotSupportedException($"Async speech model '{model}' is not supported. Use one of: {string.Join(", ", AsyncSpeechModelIds)} or '{{model}}/{{voice_id}}'.");
    }

    private static JsonElement GetObjectProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            return default;

        if (!TryGetPropertyIgnoreCase(parent, name, out var value) || value.ValueKind == JsonValueKind.Null)
            return default;

        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"providerOptions.async.{name} must be an object.");

        return value;
    }

    private static string? GetStringProperty(JsonElement parent, string name)
        => TryGetPropertyIgnoreCase(parent, name, out var value)
            ? value.Deserialize<string>(JsonSerializerOptions.Web)
            : null;

    private static int? GetInt32Property(JsonElement parent, string name)
        => TryGetPropertyIgnoreCase(parent, name, out var value)
            ? value.Deserialize<int?>(JsonSerializerOptions.Web)
            : null;

    private static double? GetDoubleProperty(JsonElement parent, string name)
        => TryGetPropertyIgnoreCase(parent, name, out var value)
            ? value.Deserialize<double?>(JsonSerializerOptions.Web)
            : null;

    private static Dictionary<string, object?> CopyObjectProperties(JsonElement element, params string[] mappedProperties)
        => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .Where(property => !mappedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone())
            : [];

}
