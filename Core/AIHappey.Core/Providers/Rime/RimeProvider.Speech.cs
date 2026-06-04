using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Rime;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    private static readonly JsonSerializerOptions RimeSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<int> OggSamplingRates = [8000, 12000, 16000, 24000];
    private static readonly HashSet<string> NewStreamingModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "coda",
        "arcana",
        "mistv3"
    };

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
        var metadata = request.GetProviderMetadata<RimeSpeechProviderMetadata>(GetIdentifier());
        var (modelId, modelVoice) = ParseModelAndVoice(request.Model);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var voice = !string.IsNullOrWhiteSpace(modelVoice)
            ? modelVoice
            : (request.Voice ?? metadata?.Voice)?.Trim();

        if (!BaseModels.Contains(modelId, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{modelId}' is not supported.");

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Rime voice is required. Use a voice-expanded model id or provide request.voice / providerOptions.rime.voice for base models.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoice)
            && !string.IsNullOrWhiteSpace(metadata?.Voice)
            && !string.Equals(metadata.Voice.Trim(), modelVoice, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.voice", reason = "voice is derived from model id" });
        }

        if (request.Speed is not null)
            warnings.Add(new { type = "ignored", feature = "speed", reason = "Rime uses providerOptions.rime.timeScaleFactor for coda, arcana, and mistv3, or providerOptions.rime.speedAlpha for mistv2" });

        var isMistV2 = modelId.Equals("mistv2", StringComparison.OrdinalIgnoreCase);
        var isNewStreamingModel = NewStreamingModels.Contains(modelId);
        var isMistV3 = modelId.Equals("mistv3", StringComparison.OrdinalIgnoreCase);

        if (isNewStreamingModel && metadata?.SpeedAlpha is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.speedAlpha", reason = "Use providerOptions.rime.timeScaleFactor for coda, arcana, and mistv3" });
        if (isMistV2 && metadata?.TimeScaleFactor is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.timeScaleFactor", reason = "Use providerOptions.rime.speedAlpha for mistv2" });
        if (!isMistV2 && metadata?.PhonemizeBetweenBrackets is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.phonemizeBetweenBrackets", reason = "Only mistv2 supports phonemizeBetweenBrackets" });
        if (!isMistV2 && metadata?.NoTextNormalization is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.noTextNormalization", reason = "Only mistv2 supports noTextNormalization" });
        if (!isMistV2 && metadata?.SaveOovs is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.saveOovs", reason = "Only mistv2 supports saveOovs" });
        if (!isMistV2 && !isMistV3 && metadata?.PauseBetweenBrackets is not null)
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.pauseBetweenBrackets", reason = "Only mistv2 and mistv3 support pauseBetweenBrackets" });
        if (!isMistV2 && !isMistV3 && !string.IsNullOrWhiteSpace(metadata?.InlineSpeedAlpha))
            warnings.Add(new { type = "ignored", feature = "providerOptions.rime.inlineSpeedAlpha", reason = "Only mistv2 and mistv3 support inlineSpeedAlpha" });

        var language = ResolveLanguage(modelId, request.Language ?? metadata?.Language);
        var audioFormat = NormalizeOutputFormat(request.OutputFormat);
        var acceptMimeType = ResolveAcceptMimeType(audioFormat);
        var samplingRate = ResolveSamplingRate(audioFormat, metadata?.SamplingRate);
        var speedAlpha = isMistV2
                ? (float?)(metadata?.SpeedAlpha ?? 1.0f)
                : null;
        var timeScaleFactor = isNewStreamingModel ? metadata?.TimeScaleFactor : null;

        var payload = BuildStreamingSpeechPayload(
            modelId,
            voice,
            request.Text,
            language,
            samplingRate,
            speedAlpha,
            timeScaleFactor,
            metadata);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rime-tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RimeSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        httpRequest.Headers.Accept.ParseAdd(acceptMimeType);

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = ResolveMimeType(resp.Content.Headers.ContentType?.ToString(), acceptMimeType);
        var resolvedFormat = ResolveFormat(mimeType, audioFormat);

        var providerMeta = new
        {
            model = modelId,
            voice,
            language,
            audioFormat,
            accept = acceptMimeType,
            samplingRate,
            speedAlpha,
            timeScaleFactor,
            pauseBetweenBrackets = metadata?.PauseBetweenBrackets ?? false,
            phonemizeBetweenBrackets = isMistV2
                ? metadata?.PhonemizeBetweenBrackets
                : null,
            inlineSpeedAlpha = metadata?.InlineSpeedAlpha,
            noTextNormalization = isMistV2
                ? (bool?)metadata?.NoTextNormalization
                : null,

            saveOovs = isMistV2
                ? (bool?)metadata?.SaveOovs
                : null,
            bytes = bytes.Length
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMeta)
            }
        };
    }

    private static (string ModelId, string? VoiceId) ParseModelAndVoice(string model)
    {
        if (!model.StartsWith(RimeModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{RimeModelPrefix}[model]' or '{RimeModelPrefix}[model]/[voiceId]'.");

        var localModel = model[RimeModelPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model must include a Rime base model after 'rime/'.", nameof(model));

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex < 0)
            return (localModel, null);

        if (slashIndex == 0 || slashIndex >= localModel.Length - 1)
            throw new ArgumentException("Rime speech model must include both base model id and voice id in the form 'rime/[baseModel]/[voiceId]'.", nameof(model));

        var modelId = localModel[..slashIndex].Trim();
        var voiceId = localModel[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Rime speech model must include both base model id and voice id in the form 'rime/[baseModel]/[voiceId]'.", nameof(model));

        return (modelId, voiceId);
    }

    private static Dictionary<string, object?> BuildStreamingSpeechPayload(
        string modelId,
        string voice,
        string text,
        string language,
        int? samplingRate,
        float? speedAlpha,
        float? timeScaleFactor,
        RimeSpeechProviderMetadata? metadata)
    {
        var payload = new Dictionary<string, object?>
        {
            ["speaker"] = voice,
            ["text"] = text,
            ["modelId"] = modelId
        };

        if (samplingRate is not null)
            payload["samplingRate"] = samplingRate;

        if (modelId.Equals("coda", StringComparison.OrdinalIgnoreCase))
        {
            payload["language"] = language;
            if (timeScaleFactor is not null)
                payload["timeScaleFactor"] = timeScaleFactor;
            return payload;
        }

        payload["lang"] = language;

        if (modelId.Equals("mistv2", StringComparison.OrdinalIgnoreCase))
        {
            payload["pauseBetweenBrackets"] = metadata?.PauseBetweenBrackets ?? false;
            payload["phonemizeBetweenBrackets"] = metadata?.PhonemizeBetweenBrackets ?? false;
            if (!string.IsNullOrWhiteSpace(metadata?.InlineSpeedAlpha))
                payload["inlineSpeedAlpha"] = metadata.InlineSpeedAlpha;
            payload["speedAlpha"] = speedAlpha ?? 1.0f;
            payload["noTextNormalization"] = metadata?.NoTextNormalization ?? false;
            payload["saveOovs"] = metadata?.SaveOovs ?? false;
            return payload;
        }

        if (timeScaleFactor is not null)
            payload["timeScaleFactor"] = timeScaleFactor;

        if (modelId.Equals("mistv3", StringComparison.OrdinalIgnoreCase))
        {
            payload["pauseBetweenBrackets"] = metadata?.PauseBetweenBrackets ?? false;
            if (!string.IsNullOrWhiteSpace(metadata?.InlineSpeedAlpha))
                payload["inlineSpeedAlpha"] = metadata.InlineSpeedAlpha;
        }

        return payload;
    }

    private static string ResolveLanguage(string modelId, string? language)
    {
        var normalized = language?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return modelId.Equals("mistv2", StringComparison.OrdinalIgnoreCase)
            ? "eng"
            : "en";
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "webm" or "opus" or "webm-opus" => "webm",
            "wav" or "wave" => "wav",
            "ogg" => "ogg",
            "pcm" or "l16" => "pcm",
            "mulaw" or "mu-law" or "ulaw" => "mulaw",
            _ => "mp3"
        };
    }

    private static string ResolveAcceptMimeType(string audioFormat)
        => audioFormat switch
        {
            "webm" => "audio/webm;codecs=opus",
            "ogg" => "audio/ogg;codecs=opus",
            "wav" => "audio/wav",
            "pcm" => "audio/L16",
            "mulaw" => "audio/PCMU",
            _ => "audio/mpeg"
        };

    private static int? ResolveSamplingRate(string audioFormat, int? samplingRate)
    {
        if (audioFormat == "ogg")
        {
            var rate = samplingRate ?? 24000;
            if (!OggSamplingRates.Contains(rate))
                throw new ArgumentOutOfRangeException(nameof(RimeSpeechProviderMetadata.SamplingRate), "Rime OGG samplingRate must be one of: 8000, 12000, 16000, 24000.");

            return rate;
        }

        if (audioFormat == "webm")
            return samplingRate ?? 24000;

        if (samplingRate is null)
            return null;

        if (samplingRate < 4000 || samplingRate > 44100)
            throw new ArgumentOutOfRangeException(nameof(RimeSpeechProviderMetadata.SamplingRate), "Rime samplingRate must be between 4000 and 44100.");

        return samplingRate;
    }

    private static string ResolveMimeType(string? contentType, string fallbackMimeType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return fallbackMimeType;
    }

    private static string ResolveFormat(string mimeType, string audioFormat)
    {
        if (!string.IsNullOrWhiteSpace(audioFormat))
            return audioFormat;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("webm")) return "webm";
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("l16")) return "pcm";
        if (mt.Contains("pcmu") || mt.Contains("basic") || mt.Contains("mulaw")) return "mulaw";
        return "mp3";
    }
}
