using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OVHcloud;

public partial class OVHcloudProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] TtsModels =
    [
        "nvr-tts-en-us",
        "nvr-tts-es-es",
        "nvr-tts-de-de",
        "nvr-tts-it-it"
    ];

    private static readonly Dictionary<string, string> TtsModelBaseUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvr-tts-en-us"] = "https://nvr-tts-en-us.endpoints.kepler.ai.cloud.ovh.net/",
        ["nvr-tts-es-es"] = "https://nvr-tts-es-es.endpoints.kepler.ai.cloud.ovh.net/",
        ["nvr-tts-de-de"] = "https://nvr-tts-de-de.endpoints.kepler.ai.cloud.ovh.net/",
        ["nvr-tts-it-it"] = "https://nvr-tts-it-it.endpoints.kepler.ai.cloud.ovh.net/"
    };

    private static bool IsSpeechModel(string model)
        => TtsModels.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveSpeechBaseUrl(string model)
    {
        foreach (var kvp in TtsModelBaseUrls)
        {
            if (model.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        }

        return null;
    }

    private static string? NormalizeOvhLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) ? null : language.Trim();

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return "wav";

        var fmt = outputFormat.Trim().ToLowerInvariant();
        if (fmt is "wave")
            return "wav";

        return fmt;
    }

    private static string MapOutputFormatToMimeType(string outputFormat)
        => outputFormat.Trim().ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };

    private static int ResolveSampleRate(SpeechRequest request)
        => 16000;

    private static int ResolveEncoding(string outputFormat)
        => 1;

    private async Task<SpeechResponse> SpeechRequestInternal(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var baseUrl = ResolveSpeechBaseUrl(request.Model);
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException($"Unknown OVHcloud speech model base URL for model '{request.Model}'.");

        var outputFormat = NormalizeOutputFormat(request.OutputFormat);
        if (!string.Equals(outputFormat, "wav", StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "unsupported", feature = "outputFormat", details = "OVHcloud TTS supports wav only. Using wav." });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["encoding"] = ResolveEncoding(outputFormat),
            ["sample_rate_hz"] = ResolveSampleRate(request),
            ["voice_name"] = request.Voice ?? "English-US.Female-1"
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
        {
            payload["language_code"] = NormalizeOvhLanguage(request.Language);
        }

        var json = JsonSerializer.Serialize(payload, SpeechJson);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), "api/v1/tts/text_to_audio"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytesOut = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytesOut);
            throw new InvalidOperationException($"OVHcloud TTS failed ({(int)resp.StatusCode}): {text}");
        }

        var mime = MapOutputFormatToMimeType("wav");
        var base64 = Convert.ToBase64String(bytesOut);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = "wav"
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
            }
        };
    }
}
