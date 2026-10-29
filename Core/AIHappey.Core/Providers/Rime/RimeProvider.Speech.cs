using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Rime;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    private static readonly JsonSerializerOptions RimeSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record RimeSpeechPlan(
        string ModelId, Uri Endpoint, Dictionary<string, object?> Payload,
        string Format, string Accept, int SamplingRate, List<object> Warnings);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var plan = PrepareSpeech(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var message = CreateSpeechMessage(plan);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        var (mimeType, format, rate) = ResolveResponseAudio(response, plan);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length == 0 || (format == "pcm" && bytes.Length % 2 != 0))
            throw new InvalidOperationException("Rime returned empty or incomplete audio.");

        var metadata = CreateSpeechMetadata(plan, mimeType, rate, bytes.Length);
        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes), MimeType = mimeType, Format = format
            },
            Warnings = plan.Warnings,
            ProviderMetadata = metadata,
            Request = new() { Body = plan.Payload },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(ProviderId),
                Body = metadata[ProviderId]
            }
        };
    }

    private static RimeSpeechPlan PrepareSpeech(SpeechRequest request, int? outputRate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        // Rime counts characters, not UTF-16 code units. Never truncate or insert text.
        if (request.Text.EnumerateRunes().Count() > 1000)
            throw new ArgumentException("Rime accepts at most 1,000 characters per HTTP speech request.", nameof(request));

        var (modelId, modelVoice) = ParseModelAndVoice(request.Model);
        if (!BaseModels.Contains(modelId, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"Rime cloud model '{modelId}' is not supported. Use coda or mistv3; Arcana and Mist v2 are legacy models.");
        modelId = modelId.ToLowerInvariant();
        var metadata = request.GetProviderMetadata<RimeSpeechProviderMetadata>(ProviderId);
        var warnings = new List<object>();
        void Ignore(string feature, string reason) => warnings.Add(new { type = "ignored", feature, reason });

        var voice = modelVoice ?? NonEmpty(request.Voice) ?? NonEmpty(metadata?.Voice);
        if (modelVoice is not null)
        {
            if (NonEmpty(request.Voice) is { } requestedVoice && !requestedVoice.Equals(modelVoice, StringComparison.Ordinal))
                Ignore("voice", "voice is derived from model id");
            if (NonEmpty(metadata?.Voice) is { } optionVoice && !optionVoice.Equals(modelVoice, StringComparison.Ordinal))
                Ignore("providerOptions.rime.voice", "voice is derived from model id");
        }
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            Ignore("instructions", "Rime HTTP speech does not support instructions");
        if (request.Speed is not null)
            Ignore("speed", "Use providerOptions.rime.timeScaleFactor; values below 1 speed up speech");
        if (metadata?.SpeedAlpha is not null)
            Ignore("providerOptions.rime.speedAlpha", "Use timeScaleFactor on Coda and Mist v3 HTTP endpoints");
        if (metadata?.NoTextNormalization is not null)
            Ignore("providerOptions.rime.noTextNormalization", "Legacy Mist option");
        if (metadata?.SaveOovs is not null)
            Ignore("providerOptions.rime.saveOovs", "Legacy Mist option");

        var language = NormalizeLanguage(NonEmpty(request.Language) ?? NonEmpty(metadata?.Language) ?? "en");
        var format = NormalizeOutputFormat(request.OutputFormat);
        var rate = outputRate ?? metadata?.SamplingRate ?? 24000;
        if (rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputRate), "Rime samplingRate must be positive.");
        if (outputRate is not null && metadata?.SamplingRate is { } optionRate && outputRate != optionRate)
            Ignore("providerOptions.rime.samplingRate", "outputFormat.rate takes precedence");

        var payload = new Dictionary<string, object?>
        {
            ["modelId"] = modelId, ["text"] = request.Text, ["lang"] = language, ["samplingRate"] = rate
        };
        // Omission invokes Coda's random voice or Mist's language-specific default.
        if (voice is not null)
            payload["speaker"] = voice;
        if (metadata?.TimeScaleFactor is { } factor)
        {
            if (!float.IsFinite(factor))
                throw new ArgumentOutOfRangeException(nameof(request), "Rime timeScaleFactor must be finite.");
            // The documented API clamps to 0.4–2.5; leave that behavior to Rime.
            payload["timeScaleFactor"] = factor;
        }

        if (modelId == "mistv3")
        {
            if (metadata?.PauseBetweenBrackets is { } pause)
                payload["pauseBetweenBrackets"] = pause;
            if (NonEmpty(metadata?.InlineSpeedAlpha) is { } inline)
                payload["inlineSpeedAlpha"] = inline;
            if (metadata?.PhonemizeBetweenBrackets is { } phonemize)
            {
                if (language == "en")
                    payload["phonemizeBetweenBrackets"] = phonemize;
                else
                    Ignore("providerOptions.rime.phonemizeBetweenBrackets", "Supported only on English Mist v3");
            }
            if (metadata?.CustomLexicon is not null)
                Ignore("providerOptions.rime.customLexicon", "Supported only on Coda");
        }
        else
        {
            if (metadata?.PauseBetweenBrackets is not null)
                Ignore("providerOptions.rime.pauseBetweenBrackets", "Supported only on Mist v3");
            if (metadata?.InlineSpeedAlpha is not null)
                Ignore("providerOptions.rime.inlineSpeedAlpha", "Supported only on Mist v3");
            if (metadata?.PhonemizeBetweenBrackets is not null)
                Ignore("providerOptions.rime.phonemizeBetweenBrackets", "Use customLexicon on Coda");
            if (metadata?.CustomLexicon is { } lexicon)
            {
                if (lexicon.Length > 500)
                    throw new ArgumentException("Rime customLexicon accepts at most 500 entries.", nameof(request));
                if (lexicon.Length > 0 && language is not ("en" or "de" or "es" or "fr" or "it" or "pt"))
                    throw new ArgumentException("Rime customLexicon supports en, de, es, fr, it, and pt only.", nameof(request));
                if (lexicon.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Spelling) || string.IsNullOrWhiteSpace(entry.Pronunciation)))
                    throw new ArgumentException("Each Rime lexicon entry requires spelling and pronunciation.", nameof(request));
                payload["customLexicon"] = lexicon;
            }
        }

        return new(modelId, new Uri(modelId == "coda" ? "https://coda.api.rime.ai/" : "https://mist.api.rime.ai/"),
            payload, format, ResolveAcceptMimeType(format), rate, warnings);
    }

    private HttpRequestMessage CreateSpeechMessage(RimeSpeechPlan plan)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, plan.Endpoint);
        try
        {
            ApplyAuthHeader(message);
            message.Content = new StringContent(JsonSerializer.Serialize(plan.Payload, RimeSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json);
            message.Headers.Accept.ParseAdd(plan.Accept);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private static async Task EnsureSpeechSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Rime TTS failed ({(int)response.StatusCode}): {error}");
        }
    }

    private static (string MimeType, string Format, int Rate) ResolveResponseAudio(HttpResponseMessage response, RimeSpeechPlan plan)
    {
        var type = response.Content.Headers.ContentType;
        var mimeType = type?.ToString() ?? plan.Accept;
        var format = NormalizeOutputFormat(type?.MediaType ?? plan.Format);
        var rate = plan.SamplingRate;
        var rateParameter = type?.Parameters.FirstOrDefault(p => p.Name.Equals("rate", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (int.TryParse(rateParameter, out var reportedRate) && reportedRate > 0)
            rate = reportedRate;
        return (mimeType, format, rate);
    }

    private static Dictionary<string, JsonElement> CreateSpeechMetadata(RimeSpeechPlan plan, string mimeType, int rate, long bytes)
        => new()
        {
            [ProviderId] = JsonSerializer.SerializeToElement(new
            {
                model = plan.ModelId,
                voice = plan.Payload.GetValueOrDefault("speaker"),
                language = plan.Payload["lang"],
                audioFormat = NormalizeOutputFormat(MediaTypeHeaderValue.Parse(mimeType).MediaType),
                accept = plan.Accept,
                samplingRate = rate,
                timeScaleFactor = plan.Payload.GetValueOrDefault("timeScaleFactor"),
                pauseBetweenBrackets = plan.Payload.GetValueOrDefault("pauseBetweenBrackets") ?? false,
                phonemizeBetweenBrackets = plan.Payload.GetValueOrDefault("phonemizeBetweenBrackets"),
                inlineSpeedAlpha = plan.Payload.GetValueOrDefault("inlineSpeedAlpha"),
                mimeType,
                bytes
            }, RimeSpeechJson)
        };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string ModelId, string? VoiceId) ParseModelAndVoice(string model)
    {
        model = model.Trim();
        if (model.StartsWith(RimeModelPrefix, StringComparison.OrdinalIgnoreCase))
            model = model[RimeModelPrefix.Length..];
        var parts = model.Split('/');
        if (parts.Length > 2 || parts.Any(part => string.IsNullOrWhiteSpace(part)))
            throw new ArgumentException("Rime model must be '[baseModel]' or '[baseModel]/[voiceId]'.", nameof(model));
        return (parts[0].Trim(), parts.Length == 2 ? parts[1].Trim() : null);
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "eng" => "en", "spa" => "es", "fra" or "fre" => "fr", "por" => "pt",
        "ger" or "deu" => "de", "jpn" => "ja", "ara" => "ar", "hin" => "hi",
        "ita" => "it", "nld" or "dut" => "nl", var value => value
    };

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var value = NonEmpty(outputFormat)?.ToLowerInvariant();
        if (value?.Contains(';') == true)
        {
            var contentType = MediaTypeHeaderValue.Parse(value);
            var codec = contentType.Parameters.FirstOrDefault(p => p.Name == "codecs")?.Value?.Trim('"');
            if (codec is not null && codec != "opus")
                throw new NotSupportedException($"Rime does not support audio codec '{codec}'.");
            if (contentType.Parameters.Any(p => !p.Name.Equals("codecs", StringComparison.OrdinalIgnoreCase)))
                throw new NotSupportedException("Rime output format parameters are not supported; set the sampling rate through outputFormat.rate or providerOptions.rime.samplingRate.");
            value = contentType.MediaType;
        }
        return value switch
        {
            null or "mp3" or "audio/mpeg" or "audio/mp3" => "mp3",
            "webm" or "opus" or "webm-opus" or "audio/webm" => "webm",
            "ogg" or "audio/ogg" => "ogg",
            "wav" or "wave" or "audio/wav" or "audio/x-wav" => "wav",
            "pcm" or "l16" or "audio/l16" or "audio/pcm" => "pcm",
            "mulaw" or "mu-law" or "ulaw" or "pcmu" or "audio/pcmu" or "audio/x-mulaw" or "audio/basic" => "mulaw",
            _ => throw new NotSupportedException($"Rime audio format '{outputFormat}' is not supported.")
        };
    }

    private static string ResolveAcceptMimeType(string format) => format switch
    {
        "webm" => "audio/webm;codecs=opus", "ogg" => "audio/ogg;codecs=opus",
        "wav" => "audio/wav", "pcm" => "audio/L16", "mulaw" => "audio/PCMU", _ => "audio/mpeg"
    };
}
