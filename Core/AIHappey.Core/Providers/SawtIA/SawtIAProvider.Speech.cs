using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SawtIA;

public partial class SawtIAProvider
{
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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (resolvedModel, modelVoiceId) = ParseSpeechModelAndVoice(request.Model);

        var metadataVoice = ReadMetadataString(metadata, "voice", "voiceId", "voice_id");
        var voiceId = (modelVoiceId ?? request.Voice ?? metadataVoice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException(
                "SawtIA requires a voice. Provide SpeechRequest.voice, providerOptions.sawtia.voice, or a model like 'sawtia/studio_pro/9000'.",
                nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoiceId, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(metadataVoice)
            && !string.Equals(modelVoiceId, metadataVoice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = $"providerOptions.{GetIdentifier()}.voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(request.OutputFormat.Trim(), "mp3", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "outputFormat", reason = "SawtIA returns mp3 audio" });
        }

        var language = (request.Language
            ?? ReadMetadataString(metadata, "language", "lang"))?.Trim();

        if (string.IsNullOrWhiteSpace(language))
            language = "en";

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["language"] = language,
            ["model"] = resolvedModel
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/text-to-speech/{Uri.EscapeDataString(voiceId)}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)response.StatusCode}): {body}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var format = ResolveSpeechFormat(contentType);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(resolvedModel, JsonSerializerOptions.Web),
            ["voice"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web),
            ["language"] = JsonSerializer.SerializeToElement(language, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            providerMetadata["contentType"] = JsonSerializer.SerializeToElement(contentType, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(contentType, format),
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = $"v1/text-to-speech/{voiceId}",
                    status = (int)response.StatusCode,
                    contentType,
                    providerModel = resolvedModel,
                    voice = voiceId,
                    language
                }
            }
        };
    }

    private (string ModelId, string? VoiceId) ParseSpeechModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 when !string.IsNullOrWhiteSpace(parts[0]) => (parts[0], null),
            2 when !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) => (parts[0], parts[1]),
            _ => throw new ArgumentException("SawtIA model must be either 'studio_pro' or 'studio_pro/{voiceId}'.", nameof(model))
        };
    }

    private static string? ReadMetadataString(JsonElement metadata, params string[] propertyNames)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(metadata, propertyName, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();

                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetRawText();
            }
        }

        return null;
    }

    private static string ResolveSpeechFormat(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "mp3";

        return contentType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/ogg" => "ogg",
            "audio/flac" => "flac",
            _ => "mp3"
        };
    }

    private static string ResolveSpeechMimeType(string? contentType, string format)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType!;

        return format switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            _ => "audio/mpeg"
        };
    }
}
