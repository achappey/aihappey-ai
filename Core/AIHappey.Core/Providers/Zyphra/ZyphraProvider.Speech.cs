using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Zyphra;

public partial class ZyphraProvider
{
    private const string ZyphraSpeechEndpoint = "v1/audio/speech";
    private const string ZyphraSpeechModel = "zyphra/ZONOS2";

    private static readonly JsonSerializerOptions ZyphraSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateZyphraSpeechRequest(request.Model, request.Text, nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var (model, shortcutVoice) = ParseZyphraSpeechModelAndVoice(request.Model);
        var payload = BuildZyphraSpeechPayload(
            model,
            shortcutVoice,
            request.Text,
            request.Voice,
            request.OutputFormat,
            request.Speed,
            TryGetZyphraOptions(request),
            stream: false,
            warnings);

        using var response = await SendZyphraSpeechRequestAsync(payload, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        EnsureZyphraSpeechSuccess(response, bytes);

        var mimeType = ResolveZyphraResponseMimeType(response.Content.Headers.ContentType?.MediaType, payload);
        var format = ResolveZyphraOutputFormat(request.OutputFormat, mimeType, ReadPayloadString(payload, "response_format"));

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = BuildZyphraProviderMetadata(model, payload, mimeType),
            Response = new()
            {
                Timestamp = now,
                ModelId = model
            }
        };
    }

    private async Task<HttpResponseMessage> SendZyphraSpeechRequestAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(Zyphra)} API key.");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ZyphraSpeechEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(ZyphraSpeechJson), Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        return await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static JsonObject BuildZyphraSpeechPayload(
        string model,
        string? shortcutVoice,
        string input,
        string? requestedVoice,
        string? outputFormat,
        float? speed,
        JsonElement? providerOptions,
        bool stream,
        List<object>? warnings = null)
    {
        var payload = new JsonObject
        {
            ["input"] = input,
            ["model"] = model,
            ["stream"] = stream
        };

        MergeZyphraOptions(payload, providerOptions);

        var providerVoice = ReadPayloadString(payload, "voice");
        var requestedVoiceValue = string.IsNullOrWhiteSpace(requestedVoice) ? null : requestedVoice.Trim();
        var voice = shortcutVoice ?? requestedVoiceValue ?? providerVoice;
        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice"] = voice;

        if (!string.IsNullOrWhiteSpace(shortcutVoice))
        {
            if (!string.IsNullOrWhiteSpace(requestedVoiceValue)
                && !string.Equals(shortcutVoice, requestedVoiceValue, StringComparison.OrdinalIgnoreCase))
            {
                warnings?.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(providerVoice)
                && !string.Equals(shortcutVoice, providerVoice, StringComparison.OrdinalIgnoreCase))
            {
                warnings?.Add(new { type = "ignored", feature = "providerOptions.zyphra.voice", reason = "voice is derived from model id" });
            }
        }

        if (speed is not null)
            payload["speed"] = speed.Value;

        var responseFormat = NormalizeZyphraResponseFormat(outputFormat);
        if (!string.IsNullOrWhiteSpace(responseFormat))
            payload["response_format"] = responseFormat;

        payload["model"] = model;
        payload["input"] = input;
        payload["stream"] = stream;
        return payload;
    }

    private static (string BaseModelId, string? VoiceId) ParseZyphraSpeechModelAndVoice(string model)
    {
        var localModel = NormalizeZyphraModelId(model);
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model is required.", nameof(model));

        if (string.Equals(localModel, ZyphraSpeechModel, StringComparison.OrdinalIgnoreCase))
            return (ZyphraSpeechModel, null);

        var prefix = ZyphraSpeechModel + "/";
        if (!localModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || localModel.Length == prefix.Length)
        {
            throw new ArgumentException(
                $"Zyphra speech model must be '{ZyphraSpeechModel}' or '{ZyphraSpeechModel}/[voice_id]'.",
                nameof(model));
        }

        return (ZyphraSpeechModel, localModel[prefix.Length..].Trim());
    }

    private static string NormalizeZyphraModelId(string model)
    {
        var trimmed = model.Trim();
        return trimmed.StartsWith("zyphra/", StringComparison.OrdinalIgnoreCase)
            ? trimmed["zyphra/".Length..]
            : trimmed;
    }

    private static JsonElement? TryGetZyphraOptions(SpeechRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(GetZyphraProviderIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return options;
    }

    private static string GetZyphraProviderIdentifier() => nameof(Zyphra).ToLowerInvariant();

    private static void MergeZyphraOptions(JsonObject payload, JsonElement? providerOptions)
    {
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.Name is "voice" or "byte_tokenize_all" or "expressive" or "max_tokens"
                or "min_p" or "prepend_silence" or "quality_buckets" or "quality_enabled"
                or "quality_values" or "reference_audio_b64" or "repetition_codebooks"
                or "repetition_penalty" or "repetition_window" or "seed" or "silence_penalty"
                or "speaker_embedding_base64" or "speaking_rate" or "speaking_rate_bucket"
                or "speaking_rate_enabled" or "temperature" or "text_norm" or "top_p" or "topk"
                or "utmos_bucket" or "utmos_enabled" or "utmos_score")
            {
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
    }

    private static void ValidateZyphraSpeechRequest(string? model, string? input, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", parameterName);
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Input is required.", parameterName);
    }

    private static void EnsureZyphraSpeechSuccess(HttpResponseMessage response, byte[] body)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zyphra TTS failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(body)}");
    }

    private static string? NormalizeZyphraResponseFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        var format = outputFormat.Trim().ToLowerInvariant();
        if (format.Contains('/'))
            return MapMimeToAudioFormat(format);

        return format switch
        {
            "mp3" or "opus" or "aac" or "flac" or "wav" or "pcm" or "m4a" => format,
            "mpeg" => "mp3",
            "mp4" => "m4a",
            _ => null
        };
    }

    private static string ResolveZyphraResponseMimeType(string? responseMimeType, JsonObject payload)
        => !string.IsNullOrWhiteSpace(responseMimeType)
            ? responseMimeType
            : MapAudioFormatToMime(ReadPayloadString(payload, "response_format")) ?? "audio/mpeg";

    private static string? ResolveZyphraOutputFormat(string? requestFormat, string? responseMimeType, string? requestedFormat)
    {
        var normalizedRequestFormat = NormalizeZyphraResponseFormat(requestFormat);
        if (!string.IsNullOrWhiteSpace(normalizedRequestFormat))
            return normalizedRequestFormat;

        if (!string.IsNullOrWhiteSpace(responseMimeType))
            return MapMimeToAudioFormat(responseMimeType.Trim().ToLowerInvariant());

        return NormalizeZyphraResponseFormat(requestedFormat) ?? "mp3";
    }

    private static string MapMimeToAudioFormat(string mimeType) => mimeType switch
    {
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/ogg" or "audio/opus" => "opus",
        "audio/aac" => "aac",
        "audio/flac" => "flac",
        "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
        "audio/l16" or "audio/pcm" => "pcm",
        "audio/mp4" => "m4a",
        _ => "mp3"
    };

    private static string? MapAudioFormatToMime(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "mp3" => "audio/mpeg",
        "opus" => "audio/opus",
        "aac" => "audio/aac",
        "flac" => "audio/flac",
        "wav" => "audio/wav",
        "pcm" => "audio/pcm",
        "m4a" => "audio/mp4",
        _ => null
    };

    private static string? ReadPayloadString(JsonObject payload, string propertyName)
        => payload[propertyName]?.GetValue<string>();

    private Dictionary<string, JsonElement> BuildZyphraProviderMetadata(string model, JsonObject payload, string? responseMimeType)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(model, ZyphraSpeechJson),
            ["payload"] = JsonSerializer.SerializeToElement(payload, ZyphraSpeechJson)
        };

        if (!string.IsNullOrWhiteSpace(responseMimeType))
            metadata["response_mime_type"] = JsonSerializer.SerializeToElement(responseMimeType, ZyphraSpeechJson);

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, ZyphraSpeechJson)
        };
    }
}
