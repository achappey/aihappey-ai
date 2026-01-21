using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Hyperbolic;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Hyperbolic;

public partial class HyperbolicProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        // Enforce the single supported model for this provider.
        // NOTE: SpeechTools strips provider prefix before dispatch.
        if (!string.Equals(request.Model.Trim(), "audio-generation", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Hyperbolic speech model '{request.Model}' is not supported. Use 'audio-generation'.");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Speaker/voice is explicitly ignored per requirements.
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });

        // Hyperbolic API returns audio as base64 (mp3). We keep our response mp3 regardless.
        if (!string.IsNullOrWhiteSpace(request.OutputFormat)
            && !string.Equals(request.OutputFormat.Trim(), "mp3", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.OutputFormat.Trim(), "mpeg", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "outputFormat", details = request.OutputFormat });
        }

        // Hyperbolic does not have an instructions field.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var metadata = request.GetSpeechProviderMetadata<HyperbolicSpeechProviderMetadata>(GetIdentifier());

        // Merge unified fields with providerOptions.
        var language = (request.Language ?? metadata?.Language)?.Trim();

        // Unified speed (SpeechRequest.speed) should be respected and can override metadata.
        var speed = request.Speed ?? metadata?.Speed;

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
        };

        if (!string.IsNullOrWhiteSpace(language))
            payload["language"] = language;

        if (metadata?.SdpRatio is not null)
            payload["sdp_ratio"] = metadata.SdpRatio.Value;

        if (metadata?.NoiseScale is not null)
            payload["noise_scale"] = metadata.NoiseScale.Value;

        if (metadata?.NoiseScaleW is not null)
            payload["noise_scale_w"] = metadata.NoiseScaleW.Value;

        if (speed is not null)
            payload["speed"] = speed.Value;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/generation")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, SpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Hyperbolic TTS failed ({(int)resp.StatusCode}): {body}");

        // Expected response: { "audio": "<base64>" }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioBase64 = root.TryGetProperty("audio", out var audioEl) && audioEl.ValueKind == JsonValueKind.String
            ? (audioEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException($"Hyperbolic TTS returned no audio. Body: {body}");

        // Always mp3 for Hyperbolic endpoint.
        const string format = "mp3";
        const string mime = "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }
}

