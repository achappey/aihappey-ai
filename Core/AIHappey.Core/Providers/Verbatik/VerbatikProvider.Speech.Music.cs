using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Verbatik;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verbatik;

public partial class VerbatikProvider
{
    private async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<VerbatikSpeechProviderMetadata>(GetIdentifier());
        var warnings = BuildMusicWarnings(request);
        var prompt = string.IsNullOrWhiteSpace(request.Text) ? null : request.Text.Trim();
        var lyrics = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions;
        var tags = metadata?.Tags?
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .ToArray();

        ValidateMusicInputs(prompt, tags, lyrics);

        var outputFormat = NormalizeMusicOutputFormat(request.OutputFormat ?? metadata?.Format ?? "mp3");
        var payload = BuildMusicPayload(request, metadata, prompt, tags, lyrics, outputFormat);

        using var resp = await _client.PostAsJsonAsync("api/v1/text-to-music", payload, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} text-to-music failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement.Clone();
        var track = ExtractFirstMusicTrack(root);
        var audio = await ResolveMusicAudioAsync(track, outputFormat, cancellationToken);
        decimal? cost = ReadDecimal(root, "cost_cents") is { } costCents
            ? costCents / 100m
            : null;

        return new SpeechResponse
        {
            Audio = audio,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(
                new
                {
                    tracks = root.TryGetProperty("tracks", out var tracksEl) ? tracksEl.Clone() : default(JsonElement?),
                    cost_cents = ReadDecimal(root, "cost_cents"),
                    balance_cents = ReadDecimal(root, "balance_cents")
                },
                costs: cost),
            Request = new()
            {
                Body = payload
            },
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static List<object> BuildMusicWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice", reason = "Verbatik text-to-music does not use the standard speech voice parameter." });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        return warnings;
    }

    private static Dictionary<string, object?> BuildMusicPayload(
        SpeechRequest request,
        VerbatikSpeechProviderMetadata? metadata,
        string? prompt,
        string[]? tags,
        string? lyrics,
        string outputFormat)
    {
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["tags"] = tags is { Length: > 0 } ? tags : null,
            ["lyrics"] = lyrics,
            ["seed"] = metadata?.Seed,
            ["prompt_strength"] = metadata?.PromptStrength ?? metadata?.PromptStrengthCamel,
            ["balance_strength"] = metadata?.BalanceStrength ?? metadata?.BalanceStrengthCamel,
            ["num_songs"] = metadata?.NumSongs ?? metadata?.NumSongsCamel,
            ["output_format"] = outputFormat,
            ["output_bit_rate"] = metadata?.OutputBitRate ?? metadata?.OutputBitRateCamel,
            ["bpm"] = NormalizeBpm(metadata?.Bpm),
            ["store_audio"] = metadata?.StoreAudio ?? metadata?.StoreAudioCamel ?? true,
            ["name"] = metadata?.Name
        };

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            payload["output_format"] = outputFormat;

        return payload
            .Where(static kvp => kvp.Value is not null)
            .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
    }

    private static object? NormalizeBpm(object? bpm)
    {
        if (bpm is null)
            return null;

        if (bpm is JsonElement json)
        {
            return json.ValueKind switch
            {
                JsonValueKind.String => json.GetString(),
                JsonValueKind.Number when json.TryGetInt32(out var number) => number,
                _ => null
            };
        }

        return bpm;
    }

    private static void ValidateMusicInputs(string? prompt, IReadOnlyCollection<string>? tags, string? lyrics)
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
        var hasTags = tags?.Count > 0;
        var hasLyrics = !string.IsNullOrWhiteSpace(lyrics);
        var count = (hasPrompt ? 1 : 0) + (hasTags == true ? 1 : 0) + (hasLyrics ? 1 : 0);

        if (count == 0)
            throw new ArgumentException("Verbatik text-to-music requires at least one of prompt, tags, or lyrics.");

        if (hasLyrics && hasPrompt != true && hasTags != true)
            throw new ArgumentException("Verbatik text-to-music lyrics must be paired with prompt or tags.");

        if (hasTags == true && !hasPrompt && !hasLyrics)
            throw new ArgumentException("Verbatik text-to-music tags must be paired with prompt or lyrics.");

        if (hasPrompt && hasTags == true && hasLyrics)
            throw new ArgumentException("Verbatik text-to-music does not allow prompt, tags, and lyrics all together.");
    }

    private static string NormalizeMusicOutputFormat(string? format)
    {
        var normalized = NormalizeFormat(format) ?? "mp3";
        return normalized switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            "mp3" or "wav" or "flac" or "ogg" or "m4a" => normalized,
            _ => "mp3"
        };
    }

    private async Task<SpeechAudioResponse> ResolveMusicAudioAsync(VerbatikMusicTrack track, string fallbackFormat, CancellationToken cancellationToken)
    {
        var format = ResolveFormatFromTrack(track, fallbackFormat);
        var mimeType = ResolveMusicMimeType(track.ContentType, track.AudioUrl, format);

        if (!string.IsNullOrWhiteSpace(track.AudioBase64))
        {
            return new SpeechAudioResponse
            {
                Base64 = StripDataUrlPrefix(track.AudioBase64, ref mimeType),
                MimeType = mimeType,
                Format = ResolveFormat(mimeType, track.AudioUrl, format)
            };
        }

        if (string.IsNullOrWhiteSpace(track.AudioUrl))
            throw new InvalidOperationException("Verbatik text-to-music response did not include an audio_url or audio base64 payload.");

        using var audioResp = await _client.GetAsync(track.AudioUrl, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} music audio download failed ({(int)audioResp.StatusCode}): {Encoding.UTF8.GetString(audioBytes)}");

        mimeType = ResolveMusicMimeType(audioResp.Content.Headers.ContentType?.MediaType ?? track.ContentType, track.AudioUrl, format);

        return new SpeechAudioResponse
        {
            Base64 = Convert.ToBase64String(audioBytes),
            MimeType = mimeType,
            Format = ResolveFormat(mimeType, track.AudioUrl, format)
        };
    }

    private static VerbatikMusicTrack ExtractFirstMusicTrack(JsonElement root)
    {
        if (!root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Verbatik text-to-music response did not include a tracks array.");

        foreach (var item in tracks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            return new VerbatikMusicTrack(
                Raw: item.Clone(),
                AudioUrl: ReadString(item, "audio_url") ?? ReadString(item, "audioUrl") ?? ReadString(item, "url"),
                AudioBase64: ReadString(item, "audio_base64") ?? ReadString(item, "audioBase64") ?? ReadString(item, "base64") ?? ReadString(item, "b64_json"),
                ContentType: ReadString(item, "mime_type") ?? ReadString(item, "mimeType") ?? ReadString(item, "content_type") ?? ReadString(item, "contentType"),
                Format: ReadString(item, "format") ?? ReadString(item, "output_format") ?? ReadString(item, "outputFormat"),
                Seed: ReadInt(item, "seed"));
        }

        throw new InvalidOperationException("Verbatik text-to-music response returned no tracks.");
    }

    private static string ResolveFormatFromTrack(VerbatikMusicTrack track, string fallbackFormat)
        => NormalizeMusicOutputFormat(track.Format ?? Path.GetExtension(track.AudioUrl ?? string.Empty).Trim('.') ?? fallbackFormat);

    private static string ResolveMusicMimeType(string? contentType, string? audioUrl, string? requestedOutputFormat)
        => ResolveMimeType(contentType, audioUrl, requestedOutputFormat) switch
        {
            "audio/mpeg" when string.Equals(NormalizeFormat(requestedOutputFormat), "m4a", StringComparison.OrdinalIgnoreCase) => "audio/mp4",
            var mime => mime
        };

    private static string StripDataUrlPrefix(string base64, ref string mimeType)
    {
        var trimmed = base64.Trim();
        var commaIndex = trimmed.IndexOf(',');
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || commaIndex < 0)
            return trimmed;

        var semicolonIndex = trimmed.IndexOf(';');
        if (semicolonIndex > "data:".Length)
            mimeType = trimmed["data:".Length..semicolonIndex];

        return trimmed[(commaIndex + 1)..];
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null
        };
    }

    private static decimal? ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value))
            return null;

        var raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    private sealed record VerbatikMusicTrack(
        JsonElement Raw,
        string? AudioUrl,
        string? AudioBase64,
        string? ContentType,
        string? Format,
        int? Seed);
}

