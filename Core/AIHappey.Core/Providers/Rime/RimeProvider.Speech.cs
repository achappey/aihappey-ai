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
            warnings.Add(new { type = "ignored", feature = "speed", reason = "Rime uses providerOptions.rime.speedAlpha" });

        var language = (request.Language ?? metadata?.Language)?.Trim();
        var audioFormat = NormalizeOutputFormat(request.OutputFormat);
        var samplingRate = ResolveSamplingRate(audioFormat, metadata?.SamplingRate);
        var speedAlpha = metadata?.SpeedAlpha ?? 1.0f;

        var payload = new Dictionary<string, object?>
        {
            ["speaker"] = voice,
            ["text"] = request.Text,
            ["modelId"] = modelId,
            ["lang"] = language ?? "eng",
            ["audioFormat"] = audioFormat,
            ["pauseBetweenBrackets"] = metadata?.PauseBetweenBrackets ?? false,
            ["phonemizeBetweenBrackets"] = metadata?.PhonemizeBetweenBrackets ?? false,
            ["inlineSpeedAlpha"] = metadata?.InlineSpeedAlpha,
            ["samplingRate"] = samplingRate,
            ["speedAlpha"] = speedAlpha,
            ["noTextNormalization"] = metadata?.NoTextNormalization ?? false,
            ["saveOovs"] = metadata?.SaveOovs ?? false
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/rime-tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RimeSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = ResolveMimeType(resp.Content.Headers.ContentType?.MediaType, audioFormat);
        var resolvedFormat = ResolveFormat(mimeType, audioFormat);

        var providerMeta = new
        {
            model = modelId,
            voice,
            language = language ?? "eng",
            audioFormat,
            samplingRate,
            speedAlpha,
            pauseBetweenBrackets = metadata?.PauseBetweenBrackets ?? false,
            phonemizeBetweenBrackets = metadata?.PhonemizeBetweenBrackets ?? false,
            inlineSpeedAlpha = metadata?.InlineSpeedAlpha,
            noTextNormalization = metadata?.NoTextNormalization ?? false,
            saveOovs = metadata?.SaveOovs ?? false,
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

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "wav" or "wave" => "wav",
            "ogg" => "ogg",
            "mulaw" or "mu-law" or "ulaw" => "mulaw",
            _ => "mp3"
        };
    }

    private static int? ResolveSamplingRate(string audioFormat, int? samplingRate)
    {
        if (audioFormat == "ogg")
        {
            var rate = samplingRate ?? 24000;
            if (!OggSamplingRates.Contains(rate))
                throw new ArgumentOutOfRangeException(nameof(RimeSpeechProviderMetadata.SamplingRate), "Rime OGG samplingRate must be one of: 8000, 12000, 16000, 24000.");

            return rate;
        }

        if (samplingRate is null)
            return null;

        if (samplingRate < 4000 || samplingRate > 44100)
            throw new ArgumentOutOfRangeException(nameof(RimeSpeechProviderMetadata.SamplingRate), "Rime samplingRate must be between 4000 and 44100.");

        return samplingRate;
    }

    private static string ResolveMimeType(string? contentType, string audioFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return audioFormat switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "mulaw" => "audio/basic",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveFormat(string mimeType, string audioFormat)
    {
        if (!string.IsNullOrWhiteSpace(audioFormat))
            return audioFormat;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("basic") || mt.Contains("mulaw")) return "mulaw";
        return "mp3";
    }
}
