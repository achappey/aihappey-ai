using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Verbatik;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
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

        var normalizedModel = request.Model;
        var voiceId = ParseVoiceIdFromModel(normalizedModel);

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "ignored", feature = "language", reason = "language is derived from selected voice model" });

        if (!string.IsNullOrWhiteSpace(request.Voice) && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var metadata = request.GetProviderMetadata<VerbatikSpeechProviderMetadata>(GetIdentifier());

        if (request.Speed is { } speed && (speed < 0.5f || speed > 2f))
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "Verbatik speed must be between 0.5 and 2.0.");

        var format = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? NormalizeFormat(request.OutputFormat)
            : NormalizeFormat(metadata?.Format);

        var contentType = ResolveRequestContentType(metadata?.ContentType, request.Text);

        using var synthRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/tts")
        {
            Content = new StringContent(request.Text, Encoding.UTF8, contentType)
        };

        synthRequest.Headers.Add("X-Voice-ID", voiceId);
        synthRequest.Headers.Add("X-Store-Audio", "false");

        if (request.Speed is { } requestSpeed)
            synthRequest.Headers.Add("X-Speed", requestSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else if (metadata?.Speed is { } metadataSpeed)
            synthRequest.Headers.Add("X-Speed", metadataSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (metadata?.Volume is { } volume)
            synthRequest.Headers.Add("X-Volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (metadata?.Pitch is { } pitch)
            synthRequest.Headers.Add("X-Pitch", pitch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(metadata?.Emotion))
            synthRequest.Headers.Add("X-Emotion", metadata.Emotion.Trim());

        if (metadata?.EnglishNormalization is { } englishNormalization)
            synthRequest.Headers.Add("X-English-Normalization", englishNormalization ? "true" : "false");

        if (metadata?.SampleRate is { } sampleRate)
            synthRequest.Headers.Add("X-Sample-Rate", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (metadata?.Bitrate is { } bitrate)
            synthRequest.Headers.Add("X-Bitrate", bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(format))
            synthRequest.Headers.Add("X-Format", format);

        if (!string.IsNullOrWhiteSpace(metadata?.LanguageBoost))
            synthRequest.Headers.Add("X-Language-Boost", metadata.LanguageBoost.Trim());

        if (metadata?.VoiceModifyPitch is { } voiceModifyPitch)
            synthRequest.Headers.Add("X-Voice-Modify-Pitch", voiceModifyPitch.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (metadata?.VoiceModifyIntensity is { } voiceModifyIntensity)
            synthRequest.Headers.Add("X-Voice-Modify-Intensity", voiceModifyIntensity.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (metadata?.VoiceModifyTimbre is { } voiceModifyTimbre)
            synthRequest.Headers.Add("X-Voice-Modify-Timbre", voiceModifyTimbre.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var synthResp = await _client.SendAsync(synthRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var synthBytes = await synthResp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!synthResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)synthResp.StatusCode}): {Encoding.UTF8.GetString(synthBytes)}");

        byte[] audioBytes;
        string mimeType;
        string resolvedFormat;
        string? audioUrl = null;
        JsonElement? synthResultElement = null;

        var responseContentType = synthResp.Content.Headers.ContentType?.MediaType;

        audioBytes = synthBytes;
        mimeType = ResolveMimeType(responseContentType, null, format);
        resolvedFormat = ResolveFormat(mimeType, null, format);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    voiceId,
                    synthResult = synthResultElement,
                    audioUrl
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(new
                {
                    voiceId,
                    synthResult = synthResultElement,
                    audioUrl
                })
            }
        };
    }


    private static string ParseVoiceIdFromModel(string model)
    {
        if (!model.StartsWith(VerbatikTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{VerbatikTtsModelPrefix}[voiceId]'.");

        var voiceId = model[VerbatikTtsModelPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must contain a voice id after 'tts/'.", nameof(model));

        return voiceId;
    }

    private static bool IsJsonContentType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static string ResolveRequestContentType(string? contentTypeMetadata, string text)
    {
        if (!string.IsNullOrWhiteSpace(contentTypeMetadata))
        {
            var ct = contentTypeMetadata.Trim();
            if (string.Equals(ct, "text/plain", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ct, "application/ssml+xml", StringComparison.OrdinalIgnoreCase))
                return ct;
        }

        return text.TrimStart().StartsWith("<speak", StringComparison.OrdinalIgnoreCase)
            ? "application/ssml+xml"
            : "text/plain";
    }

    private static string ResolveMimeType(string? contentType, string? audioUrl, string? requestedOutputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested switch
            {
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "opus" => "audio/ogg",
                "flac" => "audio/flac",
                "aac" => "audio/aac",
                "pcm" => "audio/pcm",
                _ => "audio/mpeg"
            };

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        return ext switch
        {
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "opus" => "audio/ogg",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "pcm" => "audio/pcm",
            "mp3" => "audio/mpeg",
            _ => "audio/mpeg"
        };
    }

    private static string ResolveFormat(string mimeType, string? audioUrl, string? requestedOutputFormat)
    {
        var requested = NormalizeFormat(requestedOutputFormat);
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("wav")) return "wav";
        if (mt.Contains("ogg")) return "ogg";
        if (mt.Contains("flac")) return "flac";
        if (mt.Contains("aac")) return "aac";
        if (mt.Contains("pcm")) return "pcm";

        var ext = Path.GetExtension(audioUrl ?? string.Empty).Trim('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ext))
            return ext switch
            {
                "mpeg" => "mp3",
                "wave" => "wav",
                _ => ext
            };

        return "mp3";
    }

    private static string? NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        var value = format.Trim().ToLowerInvariant();
        return value switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => value
        };
    }
}

