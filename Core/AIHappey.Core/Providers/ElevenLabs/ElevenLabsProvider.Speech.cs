using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.ElevenLabs;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (string.Equals(request.Model, "music_v1", StringComparison.OrdinalIgnoreCase))
            return await MusicRequest(request, cancellationToken);

        var metadata = request.GetSpeechProviderMetadata<ElevenLabsSpeechProviderMetadata>(GetIdentifier());

        var voice = request.Voice ?? metadata?.Voice;

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' (ElevenLabs voice_id) is required.");

        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "mp3_44100_128";

        var query = new List<string>();
        if (metadata?.EnableLogging is not null)
            query.Add($"enable_logging={metadata.EnableLogging.Value.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(outputFormat))
            query.Add($"output_format={Uri.EscapeDataString(outputFormat)}");

        var url = $"v1/text-to-speech/{Uri.EscapeDataString(voice)}" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var body = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model_id"] = request.Model,
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
            body["language_code"] = request.Language;

        if (!string.IsNullOrWhiteSpace(metadata?.PreviousText))
            body["previous_text"] = metadata.PreviousText;

        if (!string.IsNullOrWhiteSpace(metadata?.NextText))
            body["next_text"] = metadata?.NextText;

        if (!string.IsNullOrWhiteSpace(metadata?.ApplyTextNormalization))
            body["apply_text_normalization"] = metadata?.ApplyTextNormalization;

        if (metadata?.ApplyLanguageTextNormalization is not null)
            body["apply_language_text_normalization"] = metadata.ApplyLanguageTextNormalization.Value;

        if (metadata?.Seed is not null)
            body["seed"] = metadata.Seed.Value;

        if (metadata?.VoiceSettings is not null)
            body["voice_settings"] = metadata.VoiceSettings;

        using var resp = await _client.PostAsJsonAsync(url, body, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs TTS failed ({(int)resp.StatusCode}): {System.Text.Encoding.UTF8.GetString(bytes)}");

        var mime = GuessMimeType(outputFormat);
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = outputFormat?.Split("_")?.FirstOrDefault() ?? "mp3",
            },
            Warnings = [],
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model }
        };
    }

    private async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required (used as prompt).", nameof(request));

        var metadata = request.GetSpeechProviderMetadata<ElevenLabsSpeechProviderMetadata>(GetIdentifier());

        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "mp3_44100_128";

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(outputFormat))
            query.Add($"output_format={Uri.EscapeDataString(outputFormat)}");

        var url = "v1/music" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        // prompt-only implementation (no composition_plan for now)
        var body = new Dictionary<string, object?>
        {
            ["prompt"] = request.Text,
            ["model_id"] = "music_v1",
            ["music_length_ms"] = metadata?.MusicLengthMs,
            ["force_instrumental"] = metadata?.ForceInstrumental,
            ["respect_sections_durations"] = metadata?.RespectSectionsDurations,
            ["store_for_inpainting"] = metadata?.StoreForInpainting,
            ["sign_with_c2pa"] = metadata?.SignWithC2pa,
        };

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        using var resp = await _client.PostAsJsonAsync(url, body, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs Music failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mime = GuessMimeType(outputFormat);
        var base64 = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = outputFormat?.Split("_")?.FirstOrDefault() ?? "mp3",
            },
            Warnings = warnings,
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model }
        };
    }

    private static string GuessMimeType(string? outputFormat)
    {
        var fmt = (outputFormat ?? string.Empty).Trim().ToLowerInvariant();
        var codec = fmt.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? fmt;

        return codec switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/wav",
            "ulaw" or "mu-law" => "audio/basic",
            _ => "application/octet-stream"
        };
    }
}

