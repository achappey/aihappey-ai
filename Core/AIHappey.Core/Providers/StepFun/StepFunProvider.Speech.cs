using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    private static readonly JsonSerializerOptions StepFunSpeechJson = new(JsonSerializerDefaults.Web)
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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException("StepFun provider metadata must be an object.");
        var (baseModelId, modelVoiceId) = ParseSpeechModelAndVoice(request.Model);

        var metadataVoice = ReadOption<string>(metadata, "voice");
        var voice = (modelVoiceId ?? request.Voice ?? metadataVoice)?.Trim();

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadataVoice)
                && !string.Equals(metadataVoice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.stepfun.voice", reason = "voice is derived from model id" });
            }
        }

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for StepFun speech endpoint.", nameof(request));

        var metadataOutputFormat = ReadOption<string>(metadata, "response_format");
        var outputFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(metadataOutputFormat)
                ? metadataOutputFormat.Trim().ToLowerInvariant()
                : "mp3";

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var streamFormat = ReadOption<string>(metadata, "stream_format");
        if (!string.IsNullOrWhiteSpace(streamFormat) &&
            !string.Equals(streamFormat, "audio", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "stream_format" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = baseModelId,
            ["input"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = outputFormat
        };

        var speed = request.Speed ?? ReadOption<float?>(metadata, "speed");
        if (speed is not null)
            payload["speed"] = Math.Clamp(speed.Value, 0.5f, 2.0f);

        var volume = ReadOption<double?>(metadata, "volume");
        if (volume is not null)
        {
            if (volume is < 0.1 or > 2.0)
                throw new ArgumentOutOfRangeException(
                    "Volume",
                    volume,
                    "StepFun speech volume must be between 0.1 and 2.0.");

            payload["volume"] = volume.Value;
        }

        var sampleRate = ReadOption<int?>(metadata, "sample_rate");
        if (sampleRate is not null)
        {
            int[] supportedSampleRates = [8000, 16000, 22050, 24000, 48000];
            if (!supportedSampleRates.Contains(sampleRate.Value))
                throw new ArgumentOutOfRangeException(
                    "SampleRate",
                    sampleRate,
                    "StepFun speech sample rate must be one of 8000, 16000, 22050, 24000, or 48000.");

            payload["sample_rate"] = sampleRate.Value;
        }

        var voiceLabel = NormalizeVoiceLabel(GetOption(metadata, "voice_label"), request.Language);
        if (voiceLabel is not null)
            payload["voice_label"] = voiceLabel;

        var pronunciationMap = GetOption(metadata, "pronunciation_map");
        var pronunciationPayload = new Dictionary<string, object?>();
        string[]? toneValues = null;
        if (pronunciationMap is { ValueKind: JsonValueKind.Object } map)
        {
            foreach (var property in map.EnumerateObject())
                if (!property.Name.Equals("tone", StringComparison.OrdinalIgnoreCase))
                    pronunciationPayload.TryAdd(property.Name, property.Value);

            toneValues = ReadOption<string[]>(map, "tone");
        }
        else if (pronunciationMap is { ValueKind: not JsonValueKind.Null })
        {
            throw new JsonException("StepFun pronunciation_map must be an object.");
        }

        var tones = toneValues?
            .Where(tone => !string.IsNullOrWhiteSpace(tone))
            .Select(tone => tone.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tones is { Length: > 0 })
            pronunciationPayload["tone"] = tones;
        if (pronunciationPayload.Count > 0)
            payload["pronunciation_map"] = pronunciationPayload;

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (IsMappedOption(property.Name))
                    continue;

                payload.TryAdd(property.Name, property.Value);
            }
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, StepFunSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"StepFun speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = new
                {
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    contentLength = bytes.LongLength
                }
            }
        };
    }

    private (string BaseModelId, string? VoiceId) ParseSpeechModelAndVoice(string model)
    {
        var localModel = ExtractProviderLocalModelId(model);
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model is required.", nameof(model));

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex < 0)
            return (localModel, null);

        if (slashIndex == 0 || slashIndex >= localModel.Length - 1)
            throw new ArgumentException("StepFun speech model must include both base model id and voice id in the form '[baseModel]/[voiceId]'.", nameof(model));

        var baseModelId = localModel[..slashIndex].Trim();
        var voiceId = localModel[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("StepFun speech model must include both base model id and voice id in the form '[baseModel]/[voiceId]'.", nameof(model));

        return (baseModelId, voiceId);
    }

    private string ExtractProviderLocalModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var providerPrefix = GetIdentifier() + "/";
        var trimmed = modelId.Trim();

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[providerPrefix.Length..]
            : trimmed;
    }

    private static Dictionary<string, object?>? NormalizeVoiceLabel(JsonElement? label, string? requestLanguage)
    {
        if (label is null or { ValueKind: JsonValueKind.Null })
            return null;
        if (label.Value.ValueKind != JsonValueKind.Object)
            throw new JsonException("StepFun voice_label must be an object.");

        var language = ReadOption<string>(label.Value, "language");
        var emotion = ReadOption<string>(label.Value, "emotion");
        var style = ReadOption<string>(label.Value, "style");
        var normalized = new Dictionary<string, object?>();
        foreach (var property in label.Value.EnumerateObject())
            if (!IsVoiceLabelOption(property.Name))
                normalized.TryAdd(property.Name, property.Value);

        if (string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(requestLanguage))
            language = requestLanguage;

        if (!string.IsNullOrWhiteSpace(language))
            normalized["language"] = language;
        else if (!string.IsNullOrWhiteSpace(emotion))
            normalized["emotion"] = emotion;
        else if (!string.IsNullOrWhiteSpace(style))
            normalized["style"] = style;

        return normalized.Count > 0 ? normalized : null;
    }

    private static JsonElement? GetOption(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in metadata.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;

        return null;
    }

    private static T? ReadOption<T>(JsonElement metadata, string name)
        => GetOption(metadata, name) is { } value
            ? value.Deserialize<T>(StepFunSpeechJson)
            : default;

    private static bool IsMappedOption(string name)
        => name.Equals("voice", StringComparison.OrdinalIgnoreCase)
            || name.Equals("response_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("speed", StringComparison.OrdinalIgnoreCase)
            || name.Equals("volume", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sample_rate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("stream_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("voice_label", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pronunciation_map", StringComparison.OrdinalIgnoreCase);

    private static bool IsVoiceLabelOption(string name)
        => name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("emotion", StringComparison.OrdinalIgnoreCase)
            || name.Equals("style", StringComparison.OrdinalIgnoreCase);
}
