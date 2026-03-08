using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Gradium;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider
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
        var metadata = request.GetProviderMetadata<GradiumSpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceId) = ParseModelAndVoice(request.Model);

        var modelName = string.IsNullOrWhiteSpace(metadata?.ModelName)
            ? baseModelId
            : metadata.ModelName.Trim();

        var voiceId = (modelVoiceId ?? request.Voice ?? metadata?.VoiceId)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required for Gradium speech requests.", nameof(request));

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
                warnings.Add(new { type = "ignored", feature = "providerOptions.gradium.voice_id", reason = "voice is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is voice-dependent in Gradium" });

        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat) ?? "wav";
        var onlyAudio = metadata?.OnlyAudio ?? true;

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_id"] = voiceId,
            ["output_format"] = outputFormat,
            ["only_audio"] = onlyAudio
        };

        if (!string.Equals(modelName, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            payload["model_name"] = modelName;

        if (!string.IsNullOrWhiteSpace(metadata?.JsonConfig))
            payload["json_config"] = metadata.JsonConfig;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/post/speech/tts")
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
            ["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web),
            ["output_format"] = JsonSerializer.SerializeToElement(outputFormat, JsonSerializerOptions.Web),
            ["only_audio"] = JsonSerializer.SerializeToElement(onlyAudio, JsonSerializerOptions.Web)
        };

        if (!string.Equals(modelName, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            providerMetadata["model_name"] = JsonSerializer.SerializeToElement(modelName, JsonSerializerOptions.Web);

        if (!string.IsNullOrWhiteSpace(metadata?.JsonConfig))
            providerMetadata["json_config"] = JsonSerializer.SerializeToElement(metadata.JsonConfig, JsonSerializerOptions.Web);

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
                    endpoint = "api/post/speech/tts",
                    status = (int)resp.StatusCode,
                    contentType = mediaType
                }
            }
        };
    }

    private static (string BaseModelId, string? VoiceId) ParseModelAndVoice(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderId + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var slashIndex = raw.IndexOf('/');
        if (slashIndex < 0)
            return (raw, null);

        if (slashIndex == 0 || slashIndex >= raw.Length - 1)
            throw new ArgumentException("Gradium speech model must include both base model id and voice id in the form 'default/{voiceId}'.", nameof(model));

        var baseModelId = raw[..slashIndex].Trim();
        var voiceId = raw[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Gradium speech model must include both base model id and voice id in the form 'default/{voiceId}'.", nameof(model));

        return (baseModelId, voiceId);
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "wave" => "wav",
            "ogg" => "opus",
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
            "opus" => "audio/ogg",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
    }
}
