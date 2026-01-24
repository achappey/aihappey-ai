using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    private async Task<SpeechResponse> MusicRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required (used as prompt).", nameof(request));

        var metadata = request.GetProviderMetadata<ElevenLabsSpeechProviderMetadata>(GetIdentifier());

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
}

