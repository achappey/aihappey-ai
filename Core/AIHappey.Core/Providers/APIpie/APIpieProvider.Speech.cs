using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private static readonly JsonSerializerOptions SpeechSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
       AudioSpeechRequest options,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleSpeechRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent>
        OpenAISpeechStreamingAsync(
            AudioSpeechRequest options,
            CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleStreamingSpeechAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        EnsureMetadataObject(metadata);

        var provider = ReadOptionalString(metadata, "provider")?.Trim();
        if (string.IsNullOrWhiteSpace(provider))
            provider = "openai";

        var voice = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = ReadOptionalString(metadata, "voice")?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "alloy";

        var responseFormat = request.OutputFormat?.Trim();
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = ReadOptionalString(metadata, "responseFormat")?.Trim();
        if (string.IsNullOrWhiteSpace(responseFormat))
            responseFormat = "mp3";

        var voiceSettings = ReadOptionalObject(metadata, "voiceSettings");
        var speed = request.Speed;
        if (speed is { } s)
        {
            if (s < 0.25f)
            {
                warnings.Add(new { type = "unsupported", feature = "speed", details = "Minimum speed is 0.25. Using 0.25." });
                speed = 0.25f;
            }
            else if (s > 4f)
            {
                warnings.Add(new { type = "unsupported", feature = "speed", details = "Maximum speed is 4. Using 4." });
                speed = 4f;
            }
        }

        var payload = metadata.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(metadata.GetRawText())!.AsObject()
            : new JsonObject();

        RemoveProperty(payload, "provider");
        RemoveProperty(payload, "model");
        RemoveProperty(payload, "input");
        RemoveProperty(payload, "voice");
        RemoveProperty(payload, "voiceSettings");
        RemoveProperty(payload, "responseFormat");
        RemoveProperty(payload, "stream");

        payload["provider"] = provider;
        payload["model"] = request.Model;
        payload["input"] = request.Text;
        payload["voice"] = voice;
        if (voiceSettings is JsonElement settings)
            payload["voiceSettings"] = NormalizeVoiceSettings(settings);
        payload["responseFormat"] = responseFormat;
        if (speed is { } requestSpeed)
        {
            RemoveProperty(payload, "speed");
            payload["speed"] = requestSpeed;
        }
        payload["stream"] = false;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechSettings),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"APIpie TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var mime = ResolveSpeechMimeType(responseFormat, resp.Content.Headers.ContentType?.MediaType);
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = responseFormat
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

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("APIpie provider metadata must be a JSON object.");
    }

    private static string? ReadOptionalString(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        return value.Deserialize<string>(JsonSerializerOptions.Web);
    }

    private static JsonElement? ReadOptionalObject(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException($"APIpie provider metadata '{name}' must be an object.");

        ValidateOptionalNumber(value, "stability");
        ValidateOptionalNumber(value, "similarity_boost");
        ValidateOptionalNumber(value, "style");
        ValidateOptionalBoolean(value, "use_speaker_boost");
        return value;
    }

    private static JsonElement FindProperty(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return default;
    }

    private static void ValidateOptionalNumber(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        _ = value.Deserialize<double?>(JsonSerializerOptions.Web);
    }

    private static void ValidateOptionalBoolean(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        _ = value.Deserialize<bool?>(JsonSerializerOptions.Web);
    }

    private static JsonObject NormalizeVoiceSettings(JsonElement settings)
    {
        var result = JsonNode.Parse(settings.GetRawText())!.AsObject();
        CopyKnownSetting(settings, result, "stability");
        CopyKnownSetting(settings, result, "similarity_boost");
        CopyKnownSetting(settings, result, "style");
        CopyKnownSetting(settings, result, "use_speaker_boost");
        return result;
    }

    private static void CopyKnownSetting(
        JsonElement settings,
        JsonObject result,
        string name)
    {
        var value = FindProperty(settings, name);
        RemoveProperty(result, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        result[name] = JsonNode.Parse(value.GetRawText());
    }

    private static void RemoveProperty(JsonObject payload, string name)
    {
        foreach (var propertyName in payload.Select(property => property.Key)
                     .Where(propertyName => string.Equals(propertyName, name, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            payload.Remove(propertyName);
        }
    }

    private static string ResolveSpeechMimeType(string? responseFormat, string? contentType)
    {
        var fmt = (responseFormat ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            _ => contentType ?? "application/octet-stream"
        };
    }
}
