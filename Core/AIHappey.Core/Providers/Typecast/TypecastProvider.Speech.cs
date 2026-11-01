using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Typecast;

public partial class TypecastProvider
{
    private static readonly JsonSerializerOptions TypecastSpeechJson = new(JsonSerializerDefaults.Web)
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

        var (modelId, voiceId) = ParseModelAndVoiceFromModel(request.Model);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (request.Speed is { } speed)
            warnings.Add(new { type = "ignored", feature = "speed", reason = "Typecast uses output.audio_tempo via providerOptions.typecast.audioTempo" });

        var language = !string.IsNullOrWhiteSpace(request.Language)
            ? request.Language!.Trim()
            : ReadMetadata<string>(metadata, "language")?.Trim();

        var outputFormat = NormalizeOutputFormat(request.OutputFormat);

        var volume = ReadMetadata<int?>(metadata, "volume") ?? 100;
        if (volume is < 0 or > 200)
            throw new ArgumentOutOfRangeException("Volume", "Typecast volume must be between 0 and 200.");

        var audioPitch = ReadMetadata<int?>(metadata, "audioPitch") ?? 0;
        if (audioPitch is < -12 or > 12)
            throw new ArgumentOutOfRangeException("AudioPitch", "Typecast audioPitch must be between -12 and 12.");

        var audioTempo = ReadMetadata<float?>(metadata, "audioTempo") ?? 1f;
        if (audioTempo is < 0.5f or > 2f)
            throw new ArgumentOutOfRangeException("AudioTempo", "Typecast audioTempo must be between 0.5 and 2.0.");

        var prompt = BuildPrompt(metadata, modelId);
        var usesDefaultPrompt = string.Equals(modelId, "ssfm-v30", StringComparison.OrdinalIgnoreCase);
        var rawPrompt = ReadMetadataObject(metadata, "prompt", prompt is not null || usesDefaultPrompt);
        if (prompt is null && usesDefaultPrompt)
        {
            prompt = [];
            if (rawPrompt is null || !rawPrompt.ContainsKey("emotion_type"))
                prompt["emotion_type"] = "smart";
        }

        var payload = GetAdditionalMetadata(metadata,
            "language", "emotionType", "emotion", "previousText", "nextText", "volume", "audioPitch", "audioTempo", "seed");
        payload["voice_id"] = voiceId;
        payload["text"] = request.Text;
        payload["model"] = modelId;
        payload["language"] = language;
        if (prompt is not null)
        {
            rawPrompt ??= [];
            foreach (var (key, value) in prompt)
                rawPrompt[key] = value;
            payload["prompt"] = rawPrompt;
        }

        var output = ReadMetadataObject(metadata, "output", requireObject: true) ?? [];
        if (TryReadMetadata<int?>(metadata, "volume", out var configuredVolume))
            output["volume"] = configuredVolume ?? 100;
        else if (!output.ContainsKey("volume"))
            output["volume"] = volume;
        if (TryReadMetadata<int?>(metadata, "audioPitch", out var configuredPitch))
            output["audio_pitch"] = configuredPitch ?? 0;
        else if (!output.ContainsKey("audio_pitch"))
            output["audio_pitch"] = audioPitch;
        if (TryReadMetadata<float?>(metadata, "audioTempo", out var configuredTempo))
            output["audio_tempo"] = configuredTempo ?? 1f;
        else if (!output.ContainsKey("audio_tempo"))
            output["audio_tempo"] = audioTempo;
        if (!output.ContainsKey("audio_format") || !string.IsNullOrWhiteSpace(request.OutputFormat))
            output["audio_format"] = outputFormat;
        payload["output"] = output;

        if (TryReadMetadata<int?>(metadata, "seed", out var seed))
            payload["seed"] = seed;
        else if (!payload.ContainsKey("seed"))
            payload.Remove("seed");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/text-to-speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, TypecastSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = ResolveMimeType(resp.Content.Headers.ContentType?.MediaType, outputFormat);
        var resolvedFormat = ResolveFormat(mimeType, outputFormat);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Request = new()
            {
                Body = payload
            },
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static (string ModelId, string VoiceId) ParseModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(TypecastTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{TypecastTtsModelPrefix}[model]/[voiceId]'.");

        var tail = model[TypecastTtsModelPrefix.Length..].Trim();
        var slashIndex = tail.LastIndexOf('/');

        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both model id and voice id after 'typecast/'.", nameof(model));

        var modelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both model id and voice id after 'typecast/'.", nameof(model));

        return (modelId, voiceId);
    }

    private static Dictionary<string, object?>? BuildPrompt(JsonElement metadata, string modelId)
    {
        var emotionType = ReadMetadata<string>(metadata, "emotionType")?.Trim();
        var emotion = ReadMetadata<string>(metadata, "emotion")?.Trim();
        var previousText = ReadMetadata<string>(metadata, "previousText")?.Trim();
        var nextText = ReadMetadata<string>(metadata, "nextText")?.Trim();

        if (string.IsNullOrWhiteSpace(emotionType)
            && string.IsNullOrWhiteSpace(emotion)
            && string.IsNullOrWhiteSpace(previousText)
            && string.IsNullOrWhiteSpace(nextText))
            return null;

        var prompt = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(emotionType))
            prompt["emotion_type"] = emotionType;
        else if (string.Equals(modelId, "ssfm-v30", StringComparison.OrdinalIgnoreCase))
            prompt["emotion_type"] = "smart";

        if (!string.IsNullOrWhiteSpace(emotion))
            prompt["emotion"] = emotion;

        if (!string.IsNullOrWhiteSpace(previousText))
            prompt["previous_text"] = previousText;

        if (!string.IsNullOrWhiteSpace(nextText))
            prompt["next_text"] = nextText;

        return prompt.Count == 0 ? null : prompt;
    }

    private static Dictionary<string, object?> GetAdditionalMetadata(JsonElement metadata, params string[] mappedProperties)
    {
        var result = new Dictionary<string, object?>();
        if (metadata.ValueKind == JsonValueKind.Undefined || metadata.ValueKind == JsonValueKind.Null)
            return result;
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Typecast provider options must be a JSON object.");

        var mapped = new HashSet<string>(mappedProperties, StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
        {
            if (!mapped.Contains(property.Name) && !string.Equals(property.Name, "output", StringComparison.OrdinalIgnoreCase))
                result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static T? ReadMetadata<T>(JsonElement metadata, string name)
        => TryReadMetadata<T>(metadata, name, out var value) ? value : default;

    private static bool TryReadMetadata<T>(JsonElement metadata, string name, out T? value)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value.Deserialize<T>(TypecastSpeechJson);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static Dictionary<string, object?>? ReadMetadataObject(
        JsonElement metadata,
        string name,
        bool requireObject = false)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in metadata.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                if (requireObject)
                    throw new JsonException($"Typecast provider option '{name}' must be an object.");
                return null;
            }

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var nested in property.Value.EnumerateObject())
                result[nested.Name] = nested.Value.Clone();
            return result;
        }

        return null;
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "mp3" or "mpeg" => "mp3",
            _ => "wav"
        };
    }

    private static string ResolveMimeType(string? contentType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return outputFormat switch
        {
            "mp3" => "audio/mpeg",
            _ => "audio/wav"
        };
    }

    private static string ResolveFormat(string mimeType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
            return outputFormat;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("mpeg") || mt.Contains("mp3")) return "mp3";
        return "wav";
    }
}
