using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.VoiceAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.VoiceAI;

public partial class VoiceAIProvider
{
    private static readonly JsonSerializerOptions SpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<VoiceAISpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceId, modelLanguage) = ParseModelVoiceAndLanguage(request.Model);

        var voiceId = (modelVoiceId ?? request.Voice ?? metadata?.VoiceId)?.Trim();
        var language = (modelLanguage ?? request.Language ?? metadata?.Language)?.Trim()?.ToLowerInvariant();
        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? metadata?.AudioFormat) ?? "mp3";
        var temperature = metadata?.Temperature;
        var topP = metadata?.TopP;

        if (!BaseSpeechModels.Contains(baseModelId, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} speech model '{request.Model}' is not supported.");

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId)
                && !string.Equals(metadata.VoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.voiceai.voice_id", reason = "voice is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(modelLanguage))
        {
            if (!string.IsNullOrWhiteSpace(request.Language)
                && !string.Equals(request.Language.Trim(), modelLanguage, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.Language)
                && !string.Equals(metadata.Language.Trim(), modelLanguage, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.voiceai.language", reason = "language is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model"] = baseModelId,
            ["audio_format"] = outputFormat,
        };

        if (!string.IsNullOrWhiteSpace(voiceId))
            payload["voice_id"] = voiceId;

        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (temperature is not null)
            payload["temperature"] = temperature.Value;

        if (topP is not null)
            payload["top_p"] = topP.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/tts/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(baseModelId, JsonSerializerOptions.Web),
            ["audio_format"] = JsonSerializer.SerializeToElement(outputFormat, JsonSerializerOptions.Web)
        };

        if (!string.IsNullOrWhiteSpace(voiceId))
            providerMetadata["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(language))
            providerMetadata["language"] = JsonSerializer.SerializeToElement(language, JsonSerializerOptions.Web);

        if (temperature is not null)
            providerMetadata["temperature"] = JsonSerializer.SerializeToElement(temperature.Value, JsonSerializerOptions.Web);

        if (topP is not null)
            providerMetadata["top_p"] = JsonSerializer.SerializeToElement(topP.Value, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(outputFormat, mediaType),
                Format = outputFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = "api/v1/tts/speech",
                    status = (int)resp.StatusCode,
                    contentType = mediaType
                }
            }
        };
    }

    private static (string BaseModelId, string? VoiceId, string? Language) ParseModelVoiceAndLanguage(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderId + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => (parts[0], null, null),
            3 => (parts[0], parts[1], parts[2].ToLowerInvariant()),
            _ => throw new ArgumentException("VoiceAI speech model must be either '{baseModel}' or '{baseModel}/{voiceId}/{language}'.", nameof(model))
        };
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            var fmt => fmt
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
}
