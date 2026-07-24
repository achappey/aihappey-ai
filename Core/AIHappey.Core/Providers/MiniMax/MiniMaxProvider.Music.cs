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
        var normalizedModel = request.Model.Trim().ToLowerInvariant();
        var isCover = normalizedModel.StartsWith("music-cover", StringComparison.Ordinal);
        var isInstrumental = metadata?.IsInstrumental ?? false;
        var lyrics = metadata?.Lyrics?.Trim();
        var prompt = request.Text?.Trim();
        var audioBase64 = metadata?.AudioBase64?.Trim();
        var coverFeatureId = metadata?.CoverFeatureId?.Trim();

        if (isCover)
        {
            if (!string.IsNullOrWhiteSpace(audioBase64)
                && Uri.TryCreate(audioBase64, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                throw new NotSupportedException("MiniMax music cover audio must be base64 encoded; remote audio URLs are not supported.");
            }

            if (string.IsNullOrWhiteSpace(audioBase64) == string.IsNullOrWhiteSpace(coverFeatureId))
                throw new ArgumentException("MiniMax music cover requires exactly one of providerOptions.minimax.audio_base64 or providerOptions.minimax.cover_feature_id.", nameof(request));

            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length is < 10 or > 300)
                throw new ArgumentException("MiniMax music cover prompt must contain 10 to 300 characters.", nameof(request));

            if (!string.IsNullOrWhiteSpace(coverFeatureId) && (string.IsNullOrWhiteSpace(lyrics) || lyrics.Length is < 10 or > 1000))
                throw new ArgumentException("MiniMax music cover with cover_feature_id requires 10 to 1000 lyric characters.", nameof(request));
        }
        else
        {
            if (isInstrumental && (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2000))
                throw new ArgumentException("Instrumental MiniMax music requires a prompt of 1 to 2000 characters.", nameof(request));

            if (!isInstrumental && string.IsNullOrWhiteSpace(lyrics) && metadata?.LyricsOptimizer != true)
                throw new ArgumentException("Non-instrumental MiniMax music requires lyrics unless providerOptions.minimax.lyrics_optimizer is true.", nameof(request));

            if (!string.IsNullOrWhiteSpace(lyrics) && lyrics.Length > 3500)
                throw new ArgumentException("MiniMax music lyrics must not exceed 3500 characters.", nameof(request));
        }

        var format = (request.OutputFormat ?? metadata?.AudioSetting?.Format ?? "mp3").Trim().ToLowerInvariant();
        format = format is "mp3" or "wav" or "pcm" ? format : "mp3";

        return new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = prompt,
            ["lyrics"] = lyrics,
            ["stream"] = stream,
            ["output_format"] = "hex",
            ["lyrics_optimizer"] = metadata?.LyricsOptimizer,
            ["is_instrumental"] = isInstrumental,
            ["audio_base64"] = audioBase64,
            ["cover_feature_id"] = coverFeatureId,
            ["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = format,
                ["sample_rate"] = metadata?.AudioSetting?.SampleRate,
                ["bitrate"] = metadata?.AudioSetting?.Bitrate
            }
        };
    }

}
