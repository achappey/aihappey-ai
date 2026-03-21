using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider
{
    private const string DefaultTtsLanguage = "auto";
    private const string DefaultTtsVoiceId = "eve";

    private static readonly JsonSerializerOptions XaiSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        var metadata = request.GetProviderMetadata<XAISpeechProviderMetadata>(GetIdentifier());
        var selection = ParseSpeechModel(request.Model);

        var language = ResolveLanguage(selection, request, metadata, warnings);
        var voiceId = ResolveVoiceId(selection, request, metadata, warnings);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var codec = NormalizeCodec(metadata?.OutputFormat?.Codec ?? request.OutputFormat);
        var sampleRate = NormalizeSampleRate(metadata?.OutputFormat?.SampleRate);
        var bitRate = NormalizeBitRate(metadata?.OutputFormat?.BitRate);
        var outputFormat = BuildOutputFormat(codec, sampleRate, bitRate, warnings);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["language"] = language,
            ["voice_id"] = voiceId
        };

        if (outputFormat is not null)
            payload["output_format"] = outputFormat;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, XaiSpeechJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)response.StatusCode}): {body}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        var resolvedFormat = ResolveAudioFormat(codec, contentType);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["model"] = JsonSerializer.SerializeToElement(selection.BaseModelId, JsonSerializerOptions.Web),
            ["language"] = JsonSerializer.SerializeToElement(language, JsonSerializerOptions.Web),
            ["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web)
        };

        if (outputFormat is not null)
            providerMetadata["output_format"] = JsonSerializer.SerializeToElement(outputFormat, XaiSpeechJsonOptions);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(resolvedFormat, contentType),
                Format = resolvedFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = "v1/tts",
                    status = (int)response.StatusCode,
                    contentType
                }
            }
        };
    }

    private static XAITtsModelSelection ParseSpeechModel(string model)
    {
        var raw = model.Trim();
        var providerPrefix = XAIRequestExtensions.XAIIdentifier + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{ProviderName} speech model must start with '{BaseSpeechModel}'.", nameof(model));

        return parts.Length switch
        {
            1 => new XAITtsModelSelection(BaseSpeechModel, null, null),
            2 => new XAITtsModelSelection(BaseSpeechModel, NormalizeTtsLanguage(parts[1]), null),
            3 => new XAITtsModelSelection(BaseSpeechModel, NormalizeTtsLanguage(parts[1]), NormalizeTtsVoice(parts[2])),
            _ => throw new ArgumentException($"{ProviderName} speech model must be one of '{BaseSpeechModel}', '{BaseSpeechModel}/{{language}}', or '{BaseSpeechModel}/{{language}}/{{voice}}'.", nameof(model))
        };
    }

    private static string ResolveLanguage(
        XAITtsModelSelection selection,
        SpeechRequest request,
        XAISpeechProviderMetadata? metadata,
        ICollection<object> warnings)
    {
        var requestedLanguage = string.IsNullOrWhiteSpace(request.Language)
            ? null
            : NormalizeTtsLanguage(request.Language);

        var metadataLanguage = string.IsNullOrWhiteSpace(metadata?.Language)
            ? null
            : NormalizeTtsLanguage(metadata!.Language!);

        if (!string.IsNullOrWhiteSpace(selection.Language))
        {
            if (!string.IsNullOrWhiteSpace(requestedLanguage)
                && !string.Equals(requestedLanguage, selection.Language, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadataLanguage)
                && !string.Equals(metadataLanguage, selection.Language, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.xai.language", reason = "language is derived from model id" });
            }

            return selection.Language!;
        }

        return requestedLanguage
            ?? metadataLanguage
            ?? DefaultTtsLanguage;
    }

    private static string ResolveVoiceId(
        XAITtsModelSelection selection,
        SpeechRequest request,
        XAISpeechProviderMetadata? metadata,
        ICollection<object> warnings)
    {
        var requestedVoice = string.IsNullOrWhiteSpace(request.Voice)
            ? null
            : NormalizeTtsVoice(request.Voice);

        var metadataVoice = string.IsNullOrWhiteSpace(metadata?.VoiceId)
            ? null
            : NormalizeTtsVoice(metadata!.VoiceId!);

        if (!string.IsNullOrWhiteSpace(selection.VoiceId))
        {
            if (!string.IsNullOrWhiteSpace(requestedVoice)
                && !string.Equals(requestedVoice, selection.VoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadataVoice)
                && !string.Equals(metadataVoice, selection.VoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.xai.voice_id", reason = "voice is derived from model id" });
            }

            return selection.VoiceId!;
        }

        return requestedVoice
            ?? metadataVoice
            ?? DefaultTtsVoiceId;
    }

    private static Dictionary<string, object?>? BuildOutputFormat(
        string? codec,
        int? sampleRate,
        int? bitRate,
        ICollection<object> warnings)
    {
        if (string.IsNullOrWhiteSpace(codec) && sampleRate is null && bitRate is null)
            return null;

        var resolvedCodec = codec;
        if (string.IsNullOrWhiteSpace(resolvedCodec))
            resolvedCodec = bitRate is not null ? "mp3" : "wav";

        if (!string.Equals(resolvedCodec, "mp3", StringComparison.OrdinalIgnoreCase) && bitRate is not null)
        {
            warnings.Add(new { type = "ignored", feature = "providerOptions.xai.output_format.bit_rate", reason = "bit_rate only applies to mp3 codec" });
            bitRate = null;
        }

        var outputFormat = new Dictionary<string, object?>
        {
            ["codec"] = resolvedCodec
        };

        if (sampleRate is not null)
            outputFormat["sample_rate"] = sampleRate;

        if (bitRate is not null)
            outputFormat["bit_rate"] = bitRate;

        return outputFormat;
    }

    private static string? NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;

        return codec.Trim().ToLowerInvariant() switch
        {
            "wave" => "wav",
            "mp3" => "mp3",
            "wav" => "wav",
            "pcm" => "pcm",
            "mulaw" => "mulaw",
            "alaw" => "alaw",
            var unknown => throw new ArgumentException($"Unsupported xAI TTS codec '{unknown}'.", nameof(codec))
        };
    }

    private static int? NormalizeSampleRate(int? sampleRate)
    {
        if (sampleRate is null)
            return null;

        return sampleRate.Value switch
        {
            8000 or 16000 or 22050 or 24000 or 44100 or 48000 => sampleRate,
            _ => throw new ArgumentException($"Unsupported xAI TTS sample rate '{sampleRate.Value}'.", nameof(sampleRate))
        };
    }

    private static int? NormalizeBitRate(int? bitRate)
    {
        if (bitRate is null)
            return null;

        return bitRate.Value switch
        {
            32000 or 64000 or 96000 or 128000 or 192000 => bitRate,
            _ => throw new ArgumentException($"Unsupported xAI TTS bit rate '{bitRate.Value}'.", nameof(bitRate))
        };
    }

    private static string ResolveAudioFormat(string? requestedCodec, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(requestedCodec))
            return requestedCodec!;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var normalized = contentType!.Trim().ToLowerInvariant();
            if (normalized.Contains("mpeg")) return "mp3";
            if (normalized.Contains("wav") || normalized.Contains("wave")) return "wav";
            if (normalized.Contains("mulaw") || normalized.Contains("pcmu")) return "mulaw";
            if (normalized.Contains("alaw") || normalized.Contains("pcma")) return "alaw";
            if (normalized.Contains("pcm")) return "pcm";
        }

        return "mp3";
    }

    private static string ResolveSpeechMimeType(string outputFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType!;

        return outputFormat switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            "mulaw" => "audio/mulaw",
            "alaw" => "audio/alaw",
            _ => "application/octet-stream"
        };
    }

    private sealed record XAITtsModelSelection(string BaseModelId, string? Language, string? VoiceId);
}
