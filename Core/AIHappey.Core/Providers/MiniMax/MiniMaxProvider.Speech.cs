using AIHappey.Core.AI;
using AIHappey.Common.Model;
// speech
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Model.Contains("music"))
        {
            return await MusicRequest(request, cancellationToken);
        }

        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetSpeechProviderMetadata<MiniMaxSpeechProviderMetadata>(GetIdentifier());

        // ---- voice_setting ----
        var voiceId = request.Voice
            ?? metadata?.VoiceSetting?.VoiceId
            ?? "English_Insightful_Speaker";

        var speed = request.Speed
            ?? metadata?.VoiceSetting?.Speed
            ?? 1.0f;
        speed = Math.Clamp(speed, 0.5f, 2.0f);

        var vol = metadata?.VoiceSetting?.Vol ?? 1.0;
        if (vol <= 0) vol = 1.0;
        if (vol > 10) vol = 10;

        var pitch = metadata?.VoiceSetting?.Pitch ?? 0;
        if (pitch < -12) pitch = -12;
        if (pitch > 12) pitch = 12;

        // ---- audio_setting ----
        string format = (request.OutputFormat
            ?? metadata?.AudioSetting?.Format
            ?? "mp3").Trim().ToLowerInvariant();

        format = format is "mp3" or "wav" or "flac" or "pcm" ? format : "mp3";

        // Map SpeechRequest.language (usually IETF like en-US) to MiniMax language_boost enum.
        var languageBoost = metadata?.LanguageBoost ?? MapLanguageToLanguageBoost(request.Language);

        // Contract choice: we always request hex, then return a data-url.
        const string outputFormat = "hex";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["text"] = request.Text,
            ["stream"] = false,
            ["output_format"] = outputFormat,
            ["language_boost"] = languageBoost,
            ["subtitle_enable"] = metadata?.SubtitleEnable,
            ["pronunciation_dict"] = metadata?.PronunciationDict,
            ["voice_modify"] = metadata?.VoiceModify,
            ["voice_setting"] = new Dictionary<string, object?>
            {
                ["voice_id"] = voiceId,
                ["speed"] = speed,
                ["vol"] = vol,
                ["pitch"] = pitch,
                ["emotion"] = metadata?.VoiceSetting?.Emotion,
                ["text_normalization"] = metadata?.VoiceSetting?.TextNormalization,
                ["latex_read"] = metadata?.VoiceSetting?.LatexRead,
            },
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                ["sample_rate"] = metadata?.AudioSetting?.SampleRate,
                ["bitrate"] = metadata?.AudioSetting?.Bitrate,
                ["channel"] = metadata?.AudioSetting?.Channel,
                ["force_cbr"] = metadata?.AudioSetting?.ForceCbr,
            }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/t2a_v2")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax t2a_v2 failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);

        // ---- MiniMax error surface (base_resp) ----
        if (doc.RootElement.TryGetProperty("base_resp", out var baseResp) &&
            baseResp.ValueKind == JsonValueKind.Object &&
            baseResp.TryGetProperty("status_code", out var statusCodeEl) &&
            statusCodeEl.ValueKind == JsonValueKind.Number &&
            statusCodeEl.GetInt32() != 0)
        {
            var traceId = doc.RootElement.TryGetProperty("trace_id", out var traceEl) && traceEl.ValueKind == JsonValueKind.String
                ? traceEl.GetString()
                : null;

            var msg = baseResp.TryGetProperty("status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : "MiniMax request failed";

            throw new InvalidOperationException($"MiniMax t2a_v2 failed (status_code={statusCodeEl.GetInt32()}, status_msg={msg}, trace_id={traceId}).");
        }

        // ---- Extract audio hex ----
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("audio", out var audioEl) ||
            audioEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"MiniMax t2a_v2 response missing data.audio: {raw}");
        }

        var hex = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("MiniMax t2a_v2 returned empty audio.");

        var bytes = DecodeHexStringToBytes(hex);
        var mime = GuessAudioMimeType(format);
        var audioDataUrl = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioDataUrl,
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = doc.RootElement.Clone()
            }
        };
    }

    private static string? MapLanguageToLanguageBoost(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var l = language.Trim();

        if (l.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "auto";

        var primary = l.Split('-', '_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        return primary switch
        {
            "zh" => "Chinese",
            "yue" => "Chinese,Yue",
            "en" => "English",
            "ar" => "Arabic",
            "ru" => "Russian",
            "es" => "Spanish",
            "fr" => "French",
            "pt" => "Portuguese",
            "de" => "German",
            "tr" => "Turkish",
            "nl" => "Dutch",
            "uk" => "Ukrainian",
            "vi" => "Vietnamese",
            "id" => "Indonesian",
            "ja" => "Japanese",
            "it" => "Italian",
            "ko" => "Korean",
            "th" => "Thai",
            "pl" => "Polish",
            "ro" => "Romanian",
            "el" => "Greek",
            "cs" => "Czech",
            "fi" => "Finnish",
            "hi" => "Hindi",
            "bg" => "Bulgarian",
            "da" => "Danish",
            "he" => "Hebrew",
            "ms" => "Malay",
            "fa" => "Persian",
            "sk" => "Slovak",
            "sv" => "Swedish",
            "hr" => "Croatian",
            "fil" => "Filipino",
            "hu" => "Hungarian",
            "no" or "nb" => "Norwegian",
            "sl" => "Slovenian",
            "ca" => "Catalan",
            "nn" => "Nynorsk",
            "ta" => "Tamil",
            "af" => "Afrikaans",
            _ => null
        };
    }

    private static string GuessAudioMimeType(string? format)
    {
        var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            // PCM returned by MiniMax is typically raw; expose as L16 to signal PCM.
            "pcm" => "audio/L16",
            _ => "application/octet-stream"
        };
    }

    private static byte[] DecodeHexStringToBytes(string hex)
    {
        var s = hex.Trim();
        if (s.Length % 2 != 0)
            throw new InvalidOperationException("Hex audio length must be even.");

        var bytes = new byte[s.Length / 2];
        for (int i = 0, j = 0; i < s.Length; i += 2, j++)
        {
            var hi = FromHexNibble(s[i]);
            var lo = FromHexNibble(s[i + 1]);
            bytes[j] = (byte)((hi << 4) | lo);
        }
        return bytes;
    }

    private static int FromHexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        throw new InvalidOperationException($"Invalid hex character: '{c}'.");
    }
}
