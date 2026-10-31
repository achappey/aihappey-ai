using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vogent;

public partial class VogentProvider
{
    private const string ProviderName = "Vogent";
    private const string BaseSpeechModel = "tts";
    private const string MultiSpeakerBaseSpeechModel = "tts/multispeaker";

    private async Task<SpeechResponse> SpeechRequestInternal(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var prepared = PrepareSpeechRequest(request, warnings);

        using var resp = await _client.PostAsJsonAsync(prepared.Endpoint, prepared.Payload, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var resolvedFormat = ResolveSpeechFormat(prepared.Format);

        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(resolvedFormat, resp.Content.Headers.ContentType?.MediaType),
                Format = resolvedFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = prepared.Payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private PreparedVogentSpeechRequest PrepareSpeechRequest(SpeechRequest request, List<object>? warnings = null)
    {
        var rawMetadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        ValidateProviderMetadata(rawMetadata);
        var metadataVoiceId = ReadMetadataString(rawMetadata, "voiceId");
        var metadataOutputType = ReadMetadataString(rawMetadata, "outputType");
        var metadataSampleRate = ReadMetadataInt(rawMetadata, "sampleRate");
        var voiceOptionValues = ReadVoiceOptionValues(rawMetadata);
        var lines = ReadMultispeakerLines(rawMetadata);
        var model = ParseSpeechModel(request.Model);
        var payload = CopyProviderMetadata(rawMetadata);

        if (!string.IsNullOrWhiteSpace(model.VoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), model.VoiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings?.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(model.VoiceId)
            && !string.IsNullOrWhiteSpace(metadataVoiceId)
            && !string.Equals(metadataVoiceId.Trim(), model.VoiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings?.Add(new { type = "ignored", feature = "providerOptions.vogent.voiceId", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings?.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings?.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings?.Add(new { type = "unsupported", feature = "language" });

        if (model.IsMultispeaker)
        {
            ValidateMultispeakerLines(lines, nameof(request));
            payload["lines"] = lines!.Select(line => ToMultispeakerPayloadLine(line!)).ToArray();
        }
        else
        {
            var voiceId = (model.VoiceId ?? request.Voice ?? metadataVoiceId)?.Trim();
            if (string.IsNullOrWhiteSpace(voiceId))
                throw new ArgumentException("Voice is required. Provide request.voice, providerOptions.vogent.voiceId, or a model like 'vogent/tts/{voiceId}'.", nameof(request));

            payload["text"] = request.Text;
            payload["voiceId"] = voiceId;
        }

        var format = BuildOutputFormat(request.OutputFormat, metadataOutputType, metadataSampleRate);

        if (format is not null)
            payload["format"] = format;

        if (voiceOptionValues is { Count: > 0 })
            payload["voiceOptionValues"] = voiceOptionValues;

        return new PreparedVogentSpeechRequest(
            model.IsMultispeaker ? "api/tts/multispeaker" : "api/tts",
            payload,
            format);
    }

    private static string ResolveSpeechFormat(VogentOutputFormat? format)
        => format?.OutputType?.ToLowerInvariant() switch
        {
            "raw_pcm16" => "pcm",
            "wav_pcm16" => "wav",
            "mp3" => "mp3",
            _ => "wav"
        };

    private static Dictionary<string, object?> CopyProviderMetadata(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in metadata.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static JsonObject ToMultispeakerPayloadLine(JsonObject line)
    {
        var payloadLine = (JsonObject)line.DeepClone();
        var text = ReadMetadataString(line, "text")!;
        var voiceId = ReadMetadataString(line, "voiceId")!;

        RemoveJsonPropertyIgnoreCase(payloadLine, "text");
        RemoveJsonPropertyIgnoreCase(payloadLine, "voiceId");
        payloadLine["text"] = text.Trim();
        payloadLine["voiceId"] = voiceId.Trim();
        return payloadLine;
    }

    private static void ValidateMultispeakerLines(
        IReadOnlyCollection<JsonObject?>? lines,
        string parameterName)
    {
        if (lines is not { Count: > 0 })
            throw new ArgumentException("Vogent multispeaker speech requires providerOptions.vogent.lines.", parameterName);

        if (lines.Any(line => line is null
                              || string.IsNullOrWhiteSpace(ReadMetadataString(line, "text"))
                              || string.IsNullOrWhiteSpace(ReadMetadataString(line, "voiceId"))))
        {
            throw new ArgumentException("Every Vogent multispeaker line requires non-empty text and voiceId values.", parameterName);
        }
    }

    private static VogentOutputFormat? BuildOutputFormat(
        string? outputFormat,
        string? metadataOutputType,
        int? metadataSampleRate)
    {
        var normalizedOutputType = NormalizeOutputType(outputFormat ?? metadataOutputType);
        var sampleRate = metadataSampleRate;

        if (normalizedOutputType is null && sampleRate is null)
            return null;

        return new VogentOutputFormat
        {
            OutputType = normalizedOutputType ?? "WAV_PCM16",
            SampleRate = sampleRate ?? 24000
        };
    }

    private static void ValidateProviderMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)
            return;

        throw new JsonException("Vogent provider metadata must be a JSON object.");
    }

    private static string? ReadMetadataString(JsonElement metadata, string propertyName)
    {
        if (!TryGetProviderMetadataProperty(metadata, propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.Deserialize<string>(JsonSerializerOptions.Web);
    }

    private static string? ReadMetadataString(JsonObject metadata, string propertyName)
    {
        string? value = null;
        foreach (var property in metadata)
        {
            if (string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                value = property.Value?.Deserialize<string>(JsonSerializerOptions.Web);
        }

        return value;
    }

    private static int? ReadMetadataInt(JsonElement metadata, string propertyName)
    {
        if (!TryGetProviderMetadataProperty(metadata, propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.Deserialize<int?>(JsonSerializerOptions.Web);
    }

    private static List<JsonObject?>? ReadMultispeakerLines(JsonElement metadata)
    {
        if (!TryGetProviderMetadataProperty(metadata, "lines", out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        var lineElements = value.Deserialize<JsonElement[]>(JsonSerializerOptions.Web)
            ?? [];
        var lines = new List<JsonObject?>(lineElements.Length);

        foreach (var line in lineElements)
        {
            if (line.ValueKind == JsonValueKind.Null)
            {
                lines.Add(null);
                continue;
            }

            if (line.ValueKind != JsonValueKind.Object)
                throw new JsonException("Vogent multispeaker lines must contain JSON objects.");

            _ = ReadMetadataString(line, "text");
            _ = ReadMetadataString(line, "voiceId");
            lines.Add(JsonNode.Parse(line.GetRawText())!.AsObject());
        }

        return lines;
    }

    private static List<JsonObject>? ReadVoiceOptionValues(JsonElement metadata)
    {
        if (!TryGetProviderMetadataProperty(metadata, "voiceOptionValues", out var value)
            || value.ValueKind == JsonValueKind.Null)
            return null;

        var optionElements = value.Deserialize<JsonElement[]>(JsonSerializerOptions.Web)
            ?? [];
        var options = new List<JsonObject>(optionElements.Length);

        foreach (var option in optionElements)
        {
            if (option.ValueKind == JsonValueKind.Null)
                throw new NullReferenceException();
            if (option.ValueKind != JsonValueKind.Object)
                throw new JsonException("Vogent voice option values must contain JSON objects.");

            var optionId = ReadMetadataString(option, "optionId");
            var optionValue = ReadMetadataString(option, "value");
            var optionObject = JsonNode.Parse(option.GetRawText())!.AsObject();
            RemoveJsonPropertyIgnoreCase(optionObject, "optionId");
            RemoveJsonPropertyIgnoreCase(optionObject, "value");
            optionObject["optionId"] = optionId;
            optionObject["value"] = optionValue;
            options.Add(optionObject);
        }

        return options;
    }

    private static bool TryGetProviderMetadataProperty(
        JsonElement metadata,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            var found = false;
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }

            if (found)
                return true;
        }

        value = default;
        return false;
    }

    private static void RemoveJsonPropertyIgnoreCase(JsonObject obj, string propertyName)
    {
        foreach (var field in obj.Select(property => property.Key)
                     .Where(key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            obj.Remove(field);
    }

    private static string? NormalizeOutputType(string? outputType)
    {
        if (string.IsNullOrWhiteSpace(outputType))
            return null;

        return outputType.Trim().ToUpperInvariant() switch
        {
            "PCM" or "RAW" or "RAW_PCM16" => "RAW_PCM16",
            "WAV" or "WAV_PCM16" => "WAV_PCM16",
            "MP3" => "MP3",
            var value => value
        };
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

    private static VogentSpeechModel ParseSpeechModel(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderName.ToLowerInvariant() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        if (string.Equals(raw, MultiSpeakerBaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            return new VogentSpeechModel(true, null);

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 when string.Equals(parts[0], BaseSpeechModel, StringComparison.OrdinalIgnoreCase)
                => new VogentSpeechModel(false, null),
            2 when string.Equals(parts[0], BaseSpeechModel, StringComparison.OrdinalIgnoreCase)
                => new VogentSpeechModel(false, parts[1]),
            _ => throw new ArgumentException("Vogent speech model must be 'tts', 'tts/{voiceId}', or 'tts/multispeaker'.", nameof(model))
        };
    }

    private sealed record VogentSpeechModel(bool IsMultispeaker, string? VoiceId);

    private sealed record PreparedVogentSpeechRequest(
        string Endpoint,
        Dictionary<string, object?> Payload,
        VogentOutputFormat? Format);

    private sealed class VogentOutputFormat
    {
        public string OutputType { get; set; } = null!;
        public int SampleRate { get; set; }
    }
}
