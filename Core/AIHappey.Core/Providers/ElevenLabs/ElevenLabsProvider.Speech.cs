using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetSpeechProviderMetadata<ElevenLabsSpeechProviderMetadata>(GetIdentifier());

        var voice = request.Voice ?? metadata?.Voice;

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("'voice' (ElevenLabs voice_id) is required.");

        var outputFormat = request.OutputFormat ?? metadata?.OutputFormat ?? "mp3_44100_128";

        var query = new List<string>();
        if (metadata?.EnableLogging is not null)
            query.Add($"enable_logging={metadata.EnableLogging.Value.ToString().ToLowerInvariant()}");
        if (metadata?.OptimizeStreamingLatency is not null)
            query.Add($"optimize_streaming_latency={metadata.OptimizeStreamingLatency.Value}");
        if (!string.IsNullOrWhiteSpace(outputFormat))
            query.Add($"output_format={Uri.EscapeDataString(outputFormat)}");

        var url = $"v1/text-to-speech/{Uri.EscapeDataString(voice)}" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);

        var body = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["model_id"] = request.Model,
        };

        var languageCode = request.Language ?? metadata?.LanguageCode;
        if (!string.IsNullOrWhiteSpace(languageCode))
            body["language_code"] = languageCode;

        if (metadata?.Seed is not null)
            body["seed"] = metadata.Seed.Value;

        if (metadata?.VoiceSettings is not null)
            body["voice_settings"] = metadata.VoiceSettings;

        using var resp = await _client.PostAsJsonAsync(url, body, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ElevenLabs TTS failed ({(int)resp.StatusCode}): {System.Text.Encoding.UTF8.GetString(bytes)}");

        var mime = GuessMimeType(outputFormat);
        var base64 = Convert.ToBase64String(bytes).ToDataUrl(mime);

        return new SpeechResponse
        {
            Audio = base64,
            Warnings = [],
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

