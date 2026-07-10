using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private static readonly JsonSerializerOptions InworldTranscriptionJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var audioBase64 = ReadInworldTranscriptionAudioBase64(request);
        var transcribeConfig = BuildInworldTranscriptionConfig(request, providerOptions);
        var audioData = BuildInworldTranscriptionAudioData(providerOptions, audioBase64);
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["transcribeConfig"] = transcribeConfig,
            ["audioData"] = audioData
        };

        var requestJson = JsonSerializer.Serialize(payload, InworldTranscriptionJson);
        var now = DateTime.UtcNow;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "stt/v1/transcribe")
        {
            Content = new StringContent(requestJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        ApplyAuthHeader();

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Inworld transcription request failed ({(int)response.StatusCode})."
                : $"Inworld transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertInworldTranscriptionResponse(
            raw,
            request,
            transcribeConfig,
            providerOptions,
            requestJson,
            (int)response.StatusCode,
            now);
    }

    private static Dictionary<string, object?> BuildInworldTranscriptionConfig(
        TranscriptionRequest request,
        JsonElement providerOptions)
    {
        var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            if (providerOptions.TryGetProperty("transcribeConfig", out var transcribeConfig)
                && transcribeConfig.ValueKind == JsonValueKind.Object)
            {
                CopyInworldJsonObjectProperties(transcribeConfig, config);
            }

            foreach (var property in providerOptions.EnumerateObject())
            {
                if (IsInworldTranscriptionReservedProviderOption(property.Name))
                    continue;

                config[property.Name] = property.Value.Clone();
            }
        }

        if (!config.ContainsKey("modelId"))
        {
            if (string.IsNullOrWhiteSpace(request.Model))
                throw new ArgumentException("Model is required.", nameof(request));

            config["modelId"] = NormalizeInworldTranscriptionModelId(request.Model);
        }

        if (!config.ContainsKey("audioEncoding"))
        {
            var audioEncoding = ResolveInworldTranscriptionAudioEncoding(request.MediaType);
            if (string.IsNullOrWhiteSpace(audioEncoding))
                throw new ArgumentException("MediaType is required when providerOptions.inworld.audioEncoding is not supplied.", nameof(request));

            config["audioEncoding"] = audioEncoding;
        }

        return config;
    }

    private static Dictionary<string, object?> BuildInworldTranscriptionAudioData(
        JsonElement providerOptions,
        string audioBase64)
    {
        var audioData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (providerOptions.ValueKind == JsonValueKind.Object
            && providerOptions.TryGetProperty("audioData", out var audioDataOptions)
            && audioDataOptions.ValueKind == JsonValueKind.Object)
        {
            CopyInworldJsonObjectProperties(audioDataOptions, audioData);
        }

        audioData["content"] = audioBase64;
        return audioData;
    }

    private static void CopyInworldJsonObjectProperties(
        JsonElement source,
        Dictionary<string, object?> target)
    {
        foreach (var property in source.EnumerateObject())
            target[property.Name] = property.Value.Clone();
    }

    private static bool IsInworldTranscriptionReservedProviderOption(string name)
        => string.Equals(name, "transcribeConfig", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "audioData", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "audio", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "content", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "endpoint", StringComparison.OrdinalIgnoreCase);

    private static string ReadInworldTranscriptionAudioBase64(TranscriptionRequest request)
    {
        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        try
        {
            _ = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        return audioString;
    }

    private static string NormalizeInworldTranscriptionModelId(string model)
    {
        var raw = model.Trim();
        const string gatewayPrefix = "inworld/";

        if (!raw.StartsWith(gatewayPrefix, StringComparison.OrdinalIgnoreCase))
            return raw;

        var withoutGatewayPrefix = raw[gatewayPrefix.Length..];

        return withoutGatewayPrefix.Contains('/', StringComparison.Ordinal)
            ? withoutGatewayPrefix
            : raw;
    }

    private static string? ResolveInworldTranscriptionAudioEncoding(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            return null;

        var normalized = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "audio/mpeg" or "audio/mp3" or "audio/mpeg3" => "MP3",
            "audio/ogg" or "audio/opus" => "OGG_OPUS",
            "audio/flac" or "audio/x-flac" => "FLAC",
            "audio/wav" or "audio/wave" or "audio/x-wav" or "audio/l16" or "audio/pcm" => "LINEAR16",
            _ => "AUTO_DETECT"
        };
    }

    private TranscriptionResponse ConvertInworldTranscriptionResponse(
        string raw,
        TranscriptionRequest request,
        Dictionary<string, object?> transcribeConfig,
        JsonElement providerOptions,
        string requestJson,
        int statusCode,
        DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var transcription = root.TryGetProperty("transcription", out var transcriptionEl)
            && transcriptionEl.ValueKind == JsonValueKind.Object
                ? transcriptionEl
                : root;

        var segments = ExtractInworldTranscriptionSegments(root, transcription).ToList();
        var text = transcription.TryGetString("transcript", "text")
            ?? root.TryGetString("transcript", "text")
            ?? string.Join(" ", segments.Select(segment => segment.Text));

        var language = transcription.TryGetString("language", "detectedLanguage")
            ?? root.TryGetString("language", "detectedLanguage");
        var duration = TryReadInworldTranscriptionFloat(root, "durationInSeconds", "duration", "audioDurationSeconds", "seconds")
            ?? TryReadInworldTranscriptionFloat(transcription, "durationInSeconds", "duration", "audioDurationSeconds", "seconds");

        if (duration is null
            && root.TryGetProperty("usage", out var usageElement)
            && usageElement.ValueKind == JsonValueKind.Object)
        {
            duration = TryReadInworldTranscriptionFloat(usageElement, "durationInSeconds", "duration", "audioDurationSeconds", "seconds");
        }

        var responseModel = root.TryGetString("modelId", "model");
        if (string.IsNullOrWhiteSpace(responseModel)
            && transcribeConfig.TryGetValue("modelId", out var modelId))
        {
            responseModel = modelId?.ToString();
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = [],
            ProviderMetadata = BuildInworldTranscriptionProviderMetadata(
                root,
                providerOptions,
                transcribeConfig,
                statusCode),
            Request = new TranscriptionRequestItem
            {
                Body = requestJson
            },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = ResolveInworldTranscriptionResponseModelId(request.Model, responseModel),
                Body = root
            }
        };
    }

    private Dictionary<string, JsonElement> BuildInworldTranscriptionProviderMetadata(
        JsonElement responseRoot,
        JsonElement providerOptions,
        Dictionary<string, object?> transcribeConfig,
        int statusCode)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["response"] = responseRoot.Clone(),
            ["statusCode"] = statusCode,
            ["transcribeConfig"] = transcribeConfig
        };

        if (responseRoot.TryGetProperty("usage", out var usage) && usage.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            metadata["usage"] = usage.Clone();

        if (responseRoot.TryGetProperty("voiceProfile", out var voiceProfile)
            && voiceProfile.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            metadata["voiceProfile"] = voiceProfile.Clone();
        }

        if (providerOptions.ValueKind == JsonValueKind.Object)
            metadata["providerOptions"] = providerOptions.Clone();

        return GetIdentifier().CreatePrimitiveProviderMetadata(metadata);
    }

    private static IEnumerable<TranscriptionSegment> ExtractInworldTranscriptionSegments(
        JsonElement root,
        JsonElement transcription)
    {
        foreach (var segment in ExtractInworldSegmentArray(transcription, "segments"))
            yield return segment;

        if (transcription.TryGetProperty("segments", out var transcriptionSegments)
            && transcriptionSegments.ValueKind == JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var segment in ExtractInworldSegmentArray(root, "segments"))
            yield return segment;

        if (root.TryGetProperty("segments", out var rootSegments) && rootSegments.ValueKind == JsonValueKind.Array)
            yield break;

        var wordsElement = transcription.TryGetProperty("wordTimestamps", out var transcriptionWords)
            && transcriptionWords.ValueKind == JsonValueKind.Array
                ? transcriptionWords
                : root.TryGetProperty("wordTimestamps", out var rootWords)
                    && rootWords.ValueKind == JsonValueKind.Array
                        ? rootWords
                        : default;

        if (wordsElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var word in wordsElement.EnumerateArray())
        {
            var text = word.TryGetString("word", "text", "transcript") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = TryReadInworldTranscriptionFloat(word, "startSecond", "startSeconds", "startTime", "startTimeSeconds", "start") ?? 0f;
            var end = TryReadInworldTranscriptionFloat(word, "endSecond", "endSeconds", "endTime", "endTimeSeconds", "end") ?? start;

            if (end < start)
                end = start;

            yield return new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            };
        }
    }

    private static IEnumerable<TranscriptionSegment> ExtractInworldSegmentArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var segments) || segments.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var segment in segments.EnumerateArray())
        {
            var text = segment.TryGetString("text", "transcript") ?? string.Empty;
            var start = TryReadInworldTranscriptionFloat(segment, "startSecond", "startSeconds", "startTime", "startTimeSeconds", "start") ?? 0f;
            var end = TryReadInworldTranscriptionFloat(segment, "endSecond", "endSeconds", "endTime", "endTimeSeconds", "end") ?? start;

            if (end < start)
                end = start;

            yield return new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            };
        }
    }

    private static float? TryReadInworldTranscriptionFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return (float)number;

            if (value.ValueKind != JsonValueKind.String)
                continue;

            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            raw = raw.Trim().TrimEnd('s', 'S');
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string ResolveInworldTranscriptionResponseModelId(string requestModel, string? responseModel)
    {
        if (!string.IsNullOrWhiteSpace(requestModel))
            return requestModel;

        return string.IsNullOrWhiteSpace(responseModel)
            ? string.Empty
            : responseModel.ToModelId("inworld");
    }
}
