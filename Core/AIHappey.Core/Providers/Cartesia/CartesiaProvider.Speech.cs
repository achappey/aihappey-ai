using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Cartesia;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
{
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
        var metadata = request.GetProviderMetadata<CartesiaSpeechProviderMetadata>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var normalizedModel = request.Model;
        var (ttsModelId, voiceId) = ParseTtsModelAndVoiceFromModel(normalizedModel);

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var language = (request.Language ?? metadata?.Language)?.Trim();
        var container = NormalizeContainer(request.OutputFormat ?? metadata?.Container) ?? "wav";

        var speed = request.Speed ?? metadata?.Speed;
        if (speed is { } s && (s < 0.6f || s > 1.5f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Cartesia speed must be between 0.6 and 1.5.");

        if (metadata?.Volume is { } volume && (volume < 0.5f || volume > 2.0f))
            throw new ArgumentOutOfRangeException(nameof(metadata.Volume), "Cartesia volume must be between 0.5 and 2.0.");

        var isSonic3 = ttsModelId.StartsWith("sonic-3", StringComparison.OrdinalIgnoreCase);
        if (!isSonic3)
        {
            if (speed is not null)
                warnings.Add(new { type = "ignored", feature = "speed", reason = "generation_config.speed only affects sonic-3 models" });
            if (metadata?.Volume is not null)
                warnings.Add(new { type = "ignored", feature = "volume", reason = "generation_config.volume only affects sonic-3 models" });
            if (!string.IsNullOrWhiteSpace(metadata?.Emotion))
                warnings.Add(new { type = "ignored", feature = "emotion", reason = "generation_config.emotion only affects sonic-3 models" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model_id"] = ttsModelId,
            ["transcript"] = request.Text,
            ["voice"] = new Dictionary<string, object?>
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["output_format"] = BuildOutputFormat(container, metadata)
        };

        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (isSonic3)
        {
            var generationConfig = new Dictionary<string, object?>();
            if (speed is not null)
                generationConfig["speed"] = speed.Value;
            if (metadata?.Volume is not null)
                generationConfig["volume"] = metadata.Volume.Value;
            if (!string.IsNullOrWhiteSpace(metadata?.Emotion))
                generationConfig["emotion"] = metadata.Emotion!.Trim();

            if (generationConfig.Count > 0)
                payload["generation_config"] = generationConfig;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/bytes")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

        ApplyVersionHeader(httpRequest, metadata?.ApiVersion);

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = ResolveSpeechMimeType(container, resp.Content.Headers.ContentType?.MediaType);
        var format = ResolveSpeechFormat(container, mime);

        var providerMeta = new
        {
            voiceId,
            ttsModelId,
            container,
            language
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMeta)
            }
        };
    }

    private static (string TtsModelId, string VoiceId) ParseTtsModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(CartesiaTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{CartesiaTtsModelPrefix}[ttsModelId]/[voiceId]'.");

        var tail = model[CartesiaTtsModelPrefix.Length..].Trim();
        var slashIndex = tail.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both tts model id and voice id after 'tts/'.", nameof(model));

        var ttsModelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(ttsModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both tts model id and voice id after 'tts/'.", nameof(model));

        if (!SupportedTtsModelIds.Any(m => string.Equals(m, ttsModelId, StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException($"{ProviderName} TTS model '{ttsModelId}' is not supported.");

        return (ttsModelId, voiceId);
    }

    private static object BuildOutputFormat(string container, CartesiaSpeechProviderMetadata? metadata)
    {
        var normalized = NormalizeContainer(container) ?? "wav";

        if (string.Equals(normalized, "mp3", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["container"] = "mp3",
                ["sample_rate"] = metadata?.SampleRate ?? 44100,
                ["bit_rate"] = metadata?.BitRate ?? 128000
            };
        }

        if (string.Equals(normalized, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["container"] = "raw",
                ["encoding"] = NormalizeEncoding(metadata?.Encoding) ?? "pcm_s16le",
                ["sample_rate"] = metadata?.SampleRate ?? 44100
            };
        }

        return new Dictionary<string, object?>
        {
            ["container"] = "wav",
            ["encoding"] = NormalizeEncoding(metadata?.Encoding) ?? "pcm_s16le",
            ["sample_rate"] = metadata?.SampleRate ?? 44100
        };
    }

    private static string? NormalizeContainer(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
            return null;

        return container.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => container.Trim().ToLowerInvariant()
        };
    }

    private static string? NormalizeEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
            return null;

        return encoding.Trim().ToLowerInvariant();
    }

    private static string ResolveSpeechMimeType(string container, string? responseContentType)
    {
        if (!string.IsNullOrWhiteSpace(responseContentType))
            return responseContentType;

        return NormalizeContainer(container) switch
        {
            "mp3" => "audio/mpeg",
            "raw" => "audio/pcm",
            _ => "audio/wav"
        };
    }

    private static string ResolveSpeechFormat(string container, string mime)
    {
        var normalized = NormalizeContainer(container);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (mime.Contains("mpeg", StringComparison.OrdinalIgnoreCase)) return "mp3";
        if (mime.Contains("wav", StringComparison.OrdinalIgnoreCase)) return "wav";
        if (mime.Contains("pcm", StringComparison.OrdinalIgnoreCase)) return "raw";
        return "wav";
    }
}

