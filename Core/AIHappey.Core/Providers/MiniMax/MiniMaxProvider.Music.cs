using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.MiniMax;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    private static readonly HashSet<string> SupportedMiniMaxMusicModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "music-3.0",
        "music-2.6",
        "music-cover",
        "music-3.0-free",
        "music-2.6-free",
        "music-cover-free"
    };

    public async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
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

        var metadata = request.GetProviderMetadata<MiniMaxSpeechProviderMetadata>(GetIdentifier());

        var payload = BuildMusicPayload(request, metadata, stream: false);
        var format = ((Dictionary<string, object?>)payload["audio_setting"]!)["format"]!.ToString()!;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/music_generation")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax music_generation failed ({(int)resp.StatusCode}): {raw}");

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

            throw new InvalidOperationException($"MiniMax music_generation failed (status_code={statusCodeEl.GetInt32()}, status_msg={msg}, trace_id={traceId}).");
        }

        // ---- Extract audio hex ----
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("audio", out var audioEl) ||
            audioEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"MiniMax music_generation response missing data.audio: {raw}");
        }

        var hex = audioEl.GetString();
        if (string.IsNullOrWhiteSpace(hex))
            throw new InvalidOperationException("MiniMax music_generation returned empty audio.");

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
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = doc.RootElement.Clone()
            }
        };

    }

    private static Dictionary<string, object?> BuildMusicPayload(
        SpeechRequest request,
        MiniMaxSpeechProviderMetadata? metadata,
        bool stream)
    {
        var normalizedModel = NormalizeModelName(request.Model).ToLowerInvariant();
        if (!SupportedMiniMaxMusicModels.Contains(normalizedModel))
            throw new ArgumentException($"Unsupported MiniMax music model '{request.Model}'.", nameof(request));

        var isCover = normalizedModel.StartsWith("music-cover", StringComparison.Ordinal);
        var isInstrumental = metadata?.IsInstrumental ?? false;
        var lyrics = metadata?.Lyrics?.Trim();
        var prompt = request.Text?.Trim();
        var audioBase64 = metadata?.AudioBase64?.Trim();
        var audioUrl = metadata?.AudioUrl?.Trim();
        var coverFeatureId = metadata?.CoverFeatureId?.Trim();

        if (isCover)
        {
            if (!string.IsNullOrWhiteSpace(audioUrl)
                && (!Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
                throw new ArgumentException("MiniMax music cover audio_url must be an absolute HTTP or HTTPS URL.", nameof(request));

            var sourceCount = (string.IsNullOrWhiteSpace(audioUrl) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(audioBase64) ? 0 : 1)
                + (string.IsNullOrWhiteSpace(coverFeatureId) ? 0 : 1);
            if (sourceCount != 1)
                throw new ArgumentException("MiniMax music cover requires exactly one of providerOptions.minimax.audio_url, audio_base64, or cover_feature_id.", nameof(request));

            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length is < 10 or > 300)
                throw new ArgumentException("MiniMax music cover prompt must contain 10 to 300 characters.", nameof(request));

            if (!string.IsNullOrWhiteSpace(coverFeatureId) && (string.IsNullOrWhiteSpace(lyrics) || lyrics.Length is < 10 or > 1000))
                throw new ArgumentException("MiniMax music cover with cover_feature_id requires 10 to 1000 lyric characters.", nameof(request));

            if (!string.IsNullOrWhiteSpace(lyrics) && lyrics.Length is < 10 or > 1000)
                throw new ArgumentException("MiniMax music cover lyrics must contain 10 to 1000 characters.", nameof(request));

            if (metadata?.LyricsOptimizer is not null || metadata?.IsInstrumental is not null)
                throw new ArgumentException("lyrics_optimizer and is_instrumental are not supported by MiniMax music cover models.", nameof(request));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(audioUrl) || !string.IsNullOrWhiteSpace(audioBase64) || !string.IsNullOrWhiteSpace(coverFeatureId))
                throw new ArgumentException("Reference audio and cover_feature_id are only supported by MiniMax music cover models.", nameof(request));

            if (!string.IsNullOrWhiteSpace(prompt) && prompt.Length > 2000)
                throw new ArgumentException("MiniMax music prompt must not exceed 2000 characters.", nameof(request));

            if (isInstrumental && (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2000))
                throw new ArgumentException("Instrumental MiniMax music requires a prompt of 1 to 2000 characters.", nameof(request));

            if (!isInstrumental && string.IsNullOrWhiteSpace(lyrics) && metadata?.LyricsOptimizer != true)
                throw new ArgumentException("Non-instrumental MiniMax music requires lyrics unless providerOptions.minimax.lyrics_optimizer is true.", nameof(request));

            if (!string.IsNullOrWhiteSpace(lyrics) && lyrics.Length > 3500)
                throw new ArgumentException("MiniMax music lyrics must not exceed 3500 characters.", nameof(request));
        }

        var format = (request.OutputFormat ?? metadata?.AudioSetting?.Format ?? "mp3").Trim().ToLowerInvariant();
        if (format is not ("mp3" or "wav" or "pcm"))
            throw new ArgumentException("MiniMax music format must be mp3, wav, or pcm.", nameof(request));

        var sampleRate = metadata?.AudioSetting?.SampleRate;
        if (sampleRate is not null && sampleRate is not (16000 or 24000 or 32000 or 44100))
            throw new ArgumentException("MiniMax music sample_rate must be 16000, 24000, 32000, or 44100.", nameof(request));

        var bitrate = metadata?.AudioSetting?.Bitrate;
        if (bitrate is not null && bitrate is not (32000 or 64000 or 128000 or 256000))
            throw new ArgumentException("MiniMax music bitrate must be 32000, 64000, 128000, or 256000.", nameof(request));

        return new Dictionary<string, object?>
        {
            ["model"] = normalizedModel,
            ["prompt"] = prompt,
            ["lyrics"] = lyrics,
            ["stream"] = stream,
            ["output_format"] = "hex",
            ["lyrics_optimizer"] = metadata?.LyricsOptimizer,
            ["is_instrumental"] = isInstrumental,
            ["audio_url"] = audioUrl,
            ["audio_base64"] = audioBase64,
            ["cover_feature_id"] = coverFeatureId,
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                ["sample_rate"] = sampleRate,
                ["bitrate"] = bitrate
            }
        };
    }

}
