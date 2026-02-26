using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Supertone;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Supertone;

public partial class SupertoneProvider
{
    private static readonly JsonSerializerOptions SupertoneSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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

        var (ttsModelId, voiceId) = ParseModelAndVoiceFromModel(request.Model);
        var metadata = request.GetProviderMetadata<SupertoneSpeechProviderMetadata>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        var language = !string.IsNullOrWhiteSpace(request.Language)
            ? request.Language!.Trim()
            : metadata?.Language?.Trim();

        var outputFormat = NormalizeOutputFormat(request.OutputFormat);
        var style = metadata?.Style?.Trim();

        var voiceSettings = BuildVoiceSettings(request, metadata, warnings);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["language"] = language,
            ["style"] = string.IsNullOrWhiteSpace(style) ? null : style,
            ["model"] = ttsModelId,
            ["output_format"] = outputFormat,
            ["voice_settings"] = voiceSettings,
            ["include_phonemes"] = metadata?.IncludePhonemes,
            ["normalized_text"] = metadata?.NormalizedText
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/text-to-speech/{Uri.EscapeDataString(voiceId)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SupertoneSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        JsonElement? phonemes = null;
        byte[] audioBytes = bytes;

        var contentType = resp.Content.Headers.ContentType?.MediaType;
        if (!string.IsNullOrWhiteSpace(contentType)
            && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var doc = JsonDocument.Parse(bytes);

            var audioBase64 = ReadCaseInsensitiveString(doc.RootElement, "audio_base64");
            if (string.IsNullOrWhiteSpace(audioBase64))
                throw new InvalidOperationException($"{ProviderName} JSON TTS response missing audio_base64.");

            audioBytes = Convert.FromBase64String(audioBase64);

            if (TryGetCaseInsensitiveProperty(doc.RootElement, "phonemes", out var phonemesEl))
                phonemes = phonemesEl;
        }

        var mimeType = ResolveMimeType(contentType, outputFormat);
        var resolvedFormat = ResolveFormat(mimeType, outputFormat);
        var audioLengthHeader = resp.Headers.TryGetValues("X-Audio-Length", out var lengths)
            ? lengths.FirstOrDefault()
            : null;

        var providerMetaPayload = new
        {
            model = ttsModelId,
            voiceId,
            language,
            style,
            outputFormat,
            includePhonemes = metadata?.IncludePhonemes,
            normalizedText = metadata?.NormalizedText,
            audioLength = audioLengthHeader,
            voiceSettings,
            phonemes,
            bytes = audioBytes.Length
        };

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
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetaPayload)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMetaPayload)
            }
        };
    }

    private static (string TtsModelId, string VoiceId) ParseModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(SupertoneModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{SupertoneModelPrefix}[modelId]/[voiceId]'.");

        var tail = model[SupertoneModelPrefix.Length..].Trim();
        var slashIndex = tail.LastIndexOf('/');

        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both model id and voice id after 'supertone/'.", nameof(model));

        var modelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both model id and voice id after 'supertone/'.", nameof(model));

        return (modelId, voiceId);
    }

    private static Dictionary<string, object?>? BuildVoiceSettings(
        SpeechRequest request,
        SupertoneSpeechProviderMetadata? metadata,
        List<object> warnings)
    {
        var fromMetadata = metadata?.VoiceSettings;
        var speed = request.Speed ?? fromMetadata?.Speed;

        if (request.Speed is { })
            warnings.Add(new { type = "mapped", feature = "speed", target = "voice_settings.speed" });

        ValidateRange(speed, 0.5f, 2f, "Supertone speed must be between 0.5 and 2.");
        ValidateRange(fromMetadata?.PitchShift, -24f, 24f, "Supertone pitch_shift must be between -24 and 24.");
        ValidateRange(fromMetadata?.PitchVariance, 0f, 2f, "Supertone pitch_variance must be between 0 and 2.");
        ValidateRange(fromMetadata?.Duration, 0f, 60f, "Supertone duration must be between 0 and 60.");
        ValidateRange(fromMetadata?.Similarity, 1f, 5f, "Supertone similarity must be between 1 and 5.");
        ValidateRange(fromMetadata?.TextGuidance, 0f, 4f, "Supertone text_guidance must be between 0 and 4.");
        ValidateRange(fromMetadata?.SubharmonicAmplitudeControl, 0f, 2f, "Supertone subharmonic_amplitude_control must be between 0 and 2.");

        var voiceSettings = new Dictionary<string, object?>
        {
            ["pitch_shift"] = fromMetadata?.PitchShift,
            ["pitch_variance"] = fromMetadata?.PitchVariance,
            ["speed"] = speed,
            ["duration"] = fromMetadata?.Duration,
            ["similarity"] = fromMetadata?.Similarity,
            ["text_guidance"] = fromMetadata?.TextGuidance,
            ["subharmonic_amplitude_control"] = fromMetadata?.SubharmonicAmplitudeControl
        };

        foreach (var key in voiceSettings.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            voiceSettings.Remove(key);

        return voiceSettings.Count == 0 ? null : voiceSettings;
    }

    private static void ValidateRange(float? value, float min, float max, string message)
    {
        if (value is null)
            return;

        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), message);
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "mp3" or "mpeg" => "mp3",
            _ => "wav"
        };
    }

    private static string ResolveMimeType(string? contentType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return contentType;

        return outputFormat switch
        {
            "mp3" => "audio/mpeg",
            _ => "audio/wav"
        };
    }

    private static string ResolveFormat(string mimeType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
            return outputFormat;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("mpeg") || mt.Contains("mp3")) return "mp3";
        return "wav";
    }
}

