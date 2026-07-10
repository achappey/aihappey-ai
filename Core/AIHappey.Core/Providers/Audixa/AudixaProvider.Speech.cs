using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Audixa;

public partial class AudixaProvider
{

    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        var (resolvedModel, modelVoice) = ResolveModelAndVoice(request.Model);

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var rawProviderOptions = GetAudixaProviderOptions(request);
        var payload = BuildAudixaTtsPayload(request, rawProviderOptions, resolvedModel);

        var voice = (modelVoice ?? request.Voice ?? ReadString(rawProviderOptions, "voice_id"))?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Audixa requires a voice. Provide SpeechRequest.voice, an Audixa voice shortcut in the model id, or providerOptions.audixa.voice_id.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoice, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && TryGetPropertyIgnoreCase(rawProviderOptions, "voice_id", out var rawVoiceEl)
            && rawVoiceEl.ValueKind == JsonValueKind.String
            && !string.Equals(modelVoice, rawVoiceEl.GetString(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice_id", reason = "voice_id is derived from model id" });
        }

        payload["voice_id"] = voice;

        if (request.Speed is not null)
            payload["speed"] = request.Speed;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["audio_format"] = request.OutputFormat.Trim();

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language_code"] = request.Language.Trim();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/tts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Audixa TTS failed ({(int)resp.StatusCode}): {body}");

        using var createDoc = JsonDocument.Parse(body);
        var generationId = createDoc.RootElement.TryGetProperty("generation_id", out var genEl)
            ? genEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(generationId))
            throw new InvalidOperationException("Audixa TTS returned no generation_id.");

        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(60);
        var statusRoot = createDoc.RootElement.Clone();
        var status = ReadString(createDoc.RootElement, "status");
        var audioUrl = string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase)
            ? ReadString(createDoc.RootElement, "audio_url")
            : null;

        while (string.IsNullOrWhiteSpace(audioUrl) && DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            using var pollResp = await _client.GetAsync(
                $"v3/tts?generation_id={Uri.EscapeDataString(generationId)}",
                cancellationToken);

            var pollJson = await pollResp.Content.ReadAsStringAsync(cancellationToken);

            if (!pollResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Audixa TTS status failed ({(int)pollResp.StatusCode}): {pollJson}");

            using var pollDoc = JsonDocument.Parse(pollJson);
            statusRoot = pollDoc.RootElement.Clone();

            status = ReadString(pollDoc.RootElement, "status");

            if (string.Equals(status, "IN_QUEUE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "GENERATING", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                var detail = ReadString(pollDoc.RootElement, "error_message")
                    ?? ReadString(pollDoc.RootElement, "detail")
                    ?? "Unknown error";
                throw new InvalidOperationException($"Audixa TTS failed: {detail}");
            }

            if (string.Equals(status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            {
                audioUrl = ReadString(pollDoc.RootElement, "audio_url");
                break;
            }

            throw new InvalidOperationException($"Audixa TTS returned unknown status '{status ?? "null"}'.");
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            throw new TimeoutException(
                "Audixa TTS timed out waiting for completion.");
        }

        using var audioResp = await _client.GetAsync(audioUrl, cancellationToken);
        var bytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Audixa TTS audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var base64 = Convert.ToBase64String(bytes);
        var audioFormat = ResolveAudixaAudioFormat(payload, audioUrl);
        var mimeType = ResolveAudixaAudioMimeType(audioResp.Content.Headers.ContentType?.MediaType, audioFormat, audioUrl);
        var responseFormat = ResolveAudixaAudioFormat(mimeType, audioFormat, audioUrl);

        return new SpeechResponse
        {
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = mimeType,
                Format = responseFormat
            },
            Warnings = warnings,
            Request = new SpeechRequestItem
            {
                Body = payload
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = resolvedModel.ToModelId(GetIdentifier())
            }
        };
    }

    private JsonElement GetAudixaProviderOptions(SpeechRequest request)
    {
        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            && options.ValueKind == JsonValueKind.Object)
            return options;

        return default;
    }

    private static Dictionary<string, object?> BuildAudixaTtsPayload(
        SpeechRequest request,
        JsonElement providerOptions,
        string resolvedModel)
    {
        var payload = new Dictionary<string, object?>();

        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["text"] = request.Text;
        payload["model"] = resolvedModel;

        return payload;
    }

    private static void CopyProviderMetadataValue(
        Dictionary<string, JsonElement> providerMetadata,
        IReadOnlyDictionary<string, object?> payload,
        string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
            return;

        providerMetadata[key] = value is JsonElement json
            ? json.Clone()
            : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    }

    private static void CopyProviderMetadataValue(
        Dictionary<string, JsonElement> providerMetadata,
        JsonElement source,
        string key)
    {
        if (TryGetPropertyIgnoreCase(source, key, out var value))
            providerMetadata[key] = value.Clone();
    }

    private static string? ResolveAudixaAudioFormat(IReadOnlyDictionary<string, object?> payload, string audioUrl)
    {
        if (payload.TryGetValue("audio_format", out var value))
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
                return s.Trim().ToLowerInvariant();

            if (value is JsonElement { ValueKind: JsonValueKind.String } json)
            {
                var format = json.GetString();
                if (!string.IsNullOrWhiteSpace(format))
                    return format.Trim().ToLowerInvariant();
            }
        }

        return ResolveAudixaAudioFormat(null, null, audioUrl);
    }

    private static string ResolveAudixaAudioMimeType(string? contentType, string? audioFormat, string audioUrl)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return audioFormat?.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            _ => ResolveAudixaAudioFormat(null, null, audioUrl) switch
            {
                "wav" => "audio/wav",
                _ => "audio/mpeg"
            }
        };
    }

    private static string ResolveAudixaAudioFormat(string? contentType, string? requestedFormat, string audioUrl)
    {
        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var normalized = contentType.Trim().ToLowerInvariant();
            if (normalized.Contains("wav", StringComparison.Ordinal))
                return "wav";
            if (normalized.Contains("mpeg", StringComparison.Ordinal) || normalized.Contains("mp3", StringComparison.Ordinal))
                return "mp3";
        }

        var path = Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : audioUrl;

        return Path.GetExtension(path).Trim('.').ToLowerInvariant() switch
        {
            "wav" => "wav",
            "mp3" => "mp3",
            _ => "wav"
        };
    }

    private (string model, string? voiceId) ResolveModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        if (string.Equals(raw, "base", StringComparison.OrdinalIgnoreCase))
            return ("base", null);

        if (string.Equals(raw, "advanced", StringComparison.OrdinalIgnoreCase))
            return ("advanced", null);

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2
            && (string.Equals(parts[0], "base", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parts[0], "advanced", StringComparison.OrdinalIgnoreCase)))
        {
            return (parts[0].ToLowerInvariant(), parts[1]);
        }

        throw new ArgumentException("Audixa model must be 'base', 'advanced', 'base/{voiceId}', or 'advanced/{voiceId}'.", nameof(model));
    }
}

