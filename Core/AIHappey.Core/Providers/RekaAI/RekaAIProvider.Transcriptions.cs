using AIHappey.Common.Model.Providers.RekaAI;
using AIHappey.Vercel.Extensions;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RekaAI;

public partial class RekaAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var metadata = request.GetProviderMetadata<RekaAITranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Reka expects "audio_url" as an http(s) URL or data URI.
        // Unified request can carry raw base64, so convert that to data URI.
        var trimmedAudio = audioString.Trim();
        var audioUrl = trimmedAudio.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmedAudio.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmedAudio.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? trimmedAudio
            : BuildAudioDataUrl(trimmedAudio, request.MediaType);

        var payload = new Dictionary<string, object?>
        {
            ["audio_url"] = audioUrl,
            ["sampling_rate"] = metadata?.SamplingRate is > 0 ? metadata.SamplingRate.Value : 16000
        };

        if (metadata?.SamplingRate is <= 0)
        {
            warnings.Add(new
            {
                type = "invalid",
                feature = "sampling_rate",
                reason = "Must be greater than 0. Defaulted to 16000."
            });
        }

        if (!string.IsNullOrWhiteSpace(metadata?.TargetLanguage))
            payload["target_language"] = metadata.TargetLanguage;

        if (metadata?.IsTranslate is not null)
            payload["is_translate"] = metadata.IsTranslate.Value;

        if (metadata?.ReturnTranslationAudio is not null)
            payload["return_translation_audio"] = metadata.ReturnTranslationAudio.Value;

        if (metadata?.Temperature is not null)
            payload["temperature"] = metadata.Temperature.Value;

        if (metadata?.MaxTokens is > 0)
            payload["max_tokens"] = metadata.MaxTokens.Value;

        if (metadata?.MaxTokens is <= 0)
        {
            warnings.Add(new
            {
                type = "invalid",
                feature = "max_tokens",
                reason = "Must be greater than 0. Omitted from request."
            });
        }

        var body = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json);
        using var resp = await _client.PostAsync("v1/transcription_or_translation", content, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"RekaAI transcription failed ({(int)resp.StatusCode}): {json}");

        return ConvertRekaTranscriptionResponse(json, request.Model, metadata, now, warnings);
    }

    private static string BuildAudioDataUrl(string base64Audio, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            throw new ArgumentException("MediaType is required when audio is raw base64.");

        return $"data:{mediaType};base64,{base64Audio}";
    }

    private Dictionary<string, JsonElement>? BuildProviderMetadata(string? translation, string? audioBase64)
    {
        var providerMeta = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(translation))
            providerMeta["translation"] = translation;

        if (!string.IsNullOrWhiteSpace(audioBase64))
            providerMeta["audio_base64"] = audioBase64;

        if (providerMeta.Count == 0)
            return null;

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMeta, JsonSerializerOptions.Web)
        };
    }

    private TranscriptionResponse ConvertRekaTranscriptionResponse(
        string json,
        string model,
        RekaAITranscriptionProviderMetadata? metadata,
        DateTime timestamp,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("transcript_translation_with_timestamp", out var segmentsEl)
            && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                var text = seg.TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String
                    ? (t.GetString() ?? string.Empty)
                    : string.Empty;

                var start = seg.TryGetProperty("start", out var s) && TryReadFloat(s, out var sVal)
                    ? sVal
                    : 0f;

                var end = seg.TryGetProperty("end", out var e) && TryReadFloat(e, out var eVal)
                    ? eVal
                    : 0f;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = text,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        var transcript = root.TryGetProperty("transcript", out var transcriptEl) && transcriptEl.ValueKind == JsonValueKind.String
            ? (transcriptEl.GetString() ?? string.Empty)
            : string.Join(" ", segments.Select(a => a.Text));

        var translation = root.TryGetProperty("translation", out var translationEl) && translationEl.ValueKind == JsonValueKind.String
            ? translationEl.GetString()
            : null;

        var audioBase64 = root.TryGetProperty("audio_base64", out var audioBase64El) && audioBase64El.ValueKind == JsonValueKind.String
            ? audioBase64El.GetString()
            : null;

        var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
            ? languageEl.GetString()
            : metadata?.IsTranslate == true
                ? metadata.TargetLanguage
                : null;

        var duration = segments.Count > 0
            ? (float?)segments.Max(a => a.EndSecond)
            : null;

        return new TranscriptionResponse
        {
            Text = transcript,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = BuildProviderMetadata(translation, audioBase64),
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = string.IsNullOrWhiteSpace(model) ? "reka_transcription_or_translation" : model,
                Body = json
            }
        };
    }

    private static bool TryReadFloat(JsonElement el, out float value)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Number when el.TryGetDouble(out var n):
                value = (float)n;
                return true;

            case JsonValueKind.String when float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s):
                value = s;
                return true;

            default:
                value = 0f;
                return false;
        }
    }

}
