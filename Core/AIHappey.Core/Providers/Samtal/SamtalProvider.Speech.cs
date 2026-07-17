using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Samtal;

public partial class SamtalProvider
{
    private const string SamtalDefaultSpeechModel = "spectra-v1";
    private const string SamtalDefaultOutputFormat = "mp3_44100_128";

    private static readonly JsonSerializerOptions SamtalSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var selection = ResolveSamtalSpeechSelection(request.Model, request.Voice, metadata, warnings);
        var outputFormat = ResolveSamtalOutputFormat(request.OutputFormat, metadata);

        var payload = BuildSamtalSpeechPayload(
            text: request.Text,
            modelId: selection.ModelId,
            language: request.Language,
            speed: request.Speed,
            metadata,
            warnings);

        var url = BuildSamtalSpeechUrl(selection.VoiceId, stream: false, outputFormat, metadata);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SamtalSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Samtal speech failed ({(int)response.StatusCode} {response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = response.Content.Headers.ContentType?.MediaType ?? ResolveSamtalMimeType(outputFormat);
        var format = ResolveSamtalAudioFormat(outputFormat, mimeType);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = BuildSamtalProviderMetadata(selection, outputFormat),
            Response = new()
            {
                Timestamp = now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            },
            Request = new SpeechRequestItem
            {
                Body = payload
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var selection = ResolveSamtalSpeechSelection(options.Model, options.Voice, default, []);
        var outputFormat = ResolveSamtalOpenAIOutputFormat(options.ResponseFormat);
        var payload = BuildSamtalSpeechPayload(
            text: options.Input,
            modelId: selection.ModelId,
            language: null,
            speed: options.Speed,
            metadata: default,
            warnings: []);

        var url = BuildSamtalSpeechUrl(selection.VoiceId, stream: false, outputFormat, default);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SamtalSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Samtal OpenAI-compatible speech failed ({(int)response.StatusCode} {response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? ResolveSamtalMimeType(outputFormat));
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));

        var selection = ResolveSamtalSpeechSelection(options.Model, options.Voice, default, []);
        var outputFormat = ResolveSamtalOpenAIOutputFormat(options.ResponseFormat);
        var payload = BuildSamtalSpeechPayload(
            text: options.Input,
            modelId: selection.ModelId,
            language: null,
            speed: options.Speed,
            metadata: default,
            warnings: []);

        var url = BuildSamtalSpeechUrl(selection.VoiceId, stream: true, outputFormat, default);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SamtalSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Samtal OpenAI-compatible streaming speech failed ({(int)response.StatusCode} {response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static SamtalSpeechSelection ResolveSamtalSpeechSelection(
        string? model,
        string? requestedVoice,
        JsonElement metadata,
        List<object> warnings)
    {
        var localModel = NormalizeSamtalLocalModel(model);
        var modelId = localModel;
        string? modelVoice = null;

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex >= 0)
        {
            modelId = localModel[..slashIndex].Trim();
            modelVoice = localModel[(slashIndex + 1)..].Trim();

            if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(modelVoice))
                throw new ArgumentException("Samtal speech shortcut model must be in the form '[model_id]/{voice_id}'.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(modelId))
            modelId = TryGetSamtalString(metadata, "model_id", "modelId", "model") ?? SamtalDefaultSpeechModel;

        var metadataVoice = TryGetSamtalString(metadata, "voice_id", "voiceId", "voice");
        var voiceId = modelVoice ?? requestedVoice?.Trim() ?? metadataVoice;

        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required for Samtal speech. Provide request.voice, providerOptions.samtal.voice_id, or a '[model_id]/{voice_id}' model shortcut.", nameof(requestedVoice));

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(requestedVoice)
            && !string.Equals(modelVoice, requestedVoice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(metadataVoice)
            && !string.Equals(modelVoice, metadataVoice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "providerOptions.voice_id", reason = "voice is derived from model id" });
        }

        return new SamtalSpeechSelection(modelId, voiceId.Trim(), modelVoice);
    }

    private static string NormalizeSamtalLocalModel(string? model)
    {
        var localModel = string.IsNullOrWhiteSpace(model) ? SamtalDefaultSpeechModel : model.Trim();
        const string providerPrefix = "samtal/";

        if (localModel.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            localModel = localModel[providerPrefix.Length..];

        return localModel;
    }

    private static Dictionary<string, object?> BuildSamtalSpeechPayload(
        string text,
        string modelId,
        string? language,
        float? speed,
        JsonElement metadata,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["model_id"] = modelId
        };

        if (!string.IsNullOrWhiteSpace(language))
            payload["language_code"] = language.Trim();

        if (speed is not null)
            payload["speed"] = speed.Value;

        MergeSamtalProviderOptions(payload, metadata, blockedKeys:
        [
            "text",
            "input",
            "model",
            "model_id",
            "modelId",
            "voice",
            "voice_id",
            "voiceId",
            "output_format",
            "outputFormat",
            "include_word_timestamps",
            "includeWordTimestamps"
        ]);

        if (string.IsNullOrWhiteSpace(language))
        {
            var metadataLanguage = TryGetSamtalString(metadata, "language_code", "languageCode", "language");
            if (!string.IsNullOrWhiteSpace(metadataLanguage))
                payload["language_code"] = metadataLanguage.Trim();
        }

        if (speed is null && TryGetSamtalNumber(metadata, "speed") is { } metadataSpeed)
            payload["speed"] = metadataSpeed;

        if (!payload.ContainsKey("voice_settings")
            && TryGetSamtalProperty(metadata, ["voice_settings", "voiceSettings"], out var voiceSettings)
            && voiceSettings.ValueKind == JsonValueKind.Object)
        {
            payload["voice_settings"] = voiceSettings.Clone();
        }

        if (payload.TryGetValue("speed", out var speedValue)
            && TryConvertSamtalDouble(speedValue, out var speedDouble)
            && (speedDouble < 0.5 || speedDouble > 2.0))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Samtal speed must be between 0.5 and 2.0.");
        }

        return payload;
    }

    private static string BuildSamtalSpeechUrl(string voiceId, bool stream, string? outputFormat, JsonElement metadata)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(outputFormat))
            query.Add($"output_format={Uri.EscapeDataString(outputFormat)}");

        if (TryGetSamtalBool(metadata, "include_word_timestamps", "includeWordTimestamps") is { } includeWordTimestamps)
            query.Add($"include_word_timestamps={includeWordTimestamps.ToString().ToLowerInvariant()}");

        var path = $"v1/text-to-speech/{Uri.EscapeDataString(voiceId)}";
        if (stream)
            path += "/stream";

        return query.Count == 0 ? path : path + "?" + string.Join("&", query);
    }

    private static void MergeSamtalProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement metadata,
        HashSet<string>? blockedKeys = null)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (blockedKeys is not null && blockedKeys.Contains(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string ResolveSamtalOutputFormat(string? requestOutputFormat, JsonElement metadata)
        => requestOutputFormat?.Trim()
           ?? TryGetSamtalString(metadata, "output_format", "outputFormat", "format")
           ?? SamtalDefaultOutputFormat;

    private static string ResolveSamtalOpenAIOutputFormat(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return SamtalDefaultOutputFormat;

        var normalized = responseFormat.Trim().ToLowerInvariant();
        if (normalized.Contains('_', StringComparison.Ordinal))
            return normalized;

        return normalized switch
        {
            "mp3" => SamtalDefaultOutputFormat,
            _ => normalized
        };
    }

    private static string ResolveSamtalMimeType(string? outputFormat)
    {
        var codec = ResolveSamtalAudioFormat(outputFormat, null);
        return codec switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => MediaTypeNames.Application.Octet
        };
    }

    private static string ResolveSamtalAudioFormat(string? outputFormat, string? mimeType)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();
        var codec = normalized?.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(codec))
            return codec;

        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            if (mimeType.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
            if (mimeType.Contains("wav", StringComparison.OrdinalIgnoreCase) || mimeType.Contains("wave", StringComparison.OrdinalIgnoreCase)) return "wav";
            if (mimeType.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "pcm";
            if (mimeType.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "opus";
            if (mimeType.Contains("aac", StringComparison.OrdinalIgnoreCase)) return "aac";
            if (mimeType.Contains("flac", StringComparison.OrdinalIgnoreCase)) return "flac";
        }

        return "mp3";
    }

    private static Dictionary<string, JsonElement> BuildSamtalProviderMetadata(SamtalSpeechSelection selection, string? outputFormat)
        => nameof(Samtal).ToLowerInvariant().CreatePrimitiveProviderMetadata(new
        {
            model_id = selection.ModelId,
            voice_id = selection.VoiceId,
            output_format = outputFormat
        });

    private static string? TryGetSamtalString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (TryGetSamtalProperty(element, name, out var property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();
        }

        return null;
    }

    private static double? TryGetSamtalNumber(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!TryGetSamtalProperty(element, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
                return value;

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static bool? TryGetSamtalBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!TryGetSamtalProperty(element, name, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.True) return true;
            if (property.ValueKind == JsonValueKind.False) return false;
            if (property.ValueKind == JsonValueKind.String && bool.TryParse(property.GetString(), out var parsed)) return parsed;
        }

        return null;
    }

    private static bool TryGetSamtalProperty(JsonElement element, string name, out JsonElement property)
        => TryGetSamtalProperty(element, [name], out property);

    private static bool TryGetSamtalProperty(JsonElement element, string[] names, out JsonElement property)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            property = default;
            return false;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(name, prop.Name, StringComparison.OrdinalIgnoreCase)))
            {
                property = prop.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static bool TryConvertSamtalDouble(object? value, out double result)
    {
        switch (value)
        {
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDouble(out var elementValue):
                result = elementValue;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element
                when double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private sealed record SamtalSpeechSelection(string ModelId, string VoiceId, string? ModelVoiceId);

}
