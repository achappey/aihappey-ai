using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers;
using System.Text.Json;
using System.Text;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private async Task<SpeechResponse> SpeechRequestGlmTts(
         SpeechRequest request,
         CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var metadata =
            request.GetSpeechProviderMetadata<NovitaSpeechProviderMetadata>(GetIdentifier());

        var voice =
            request.Voice
            ?? metadata?.Glm?.Voice
            ?? "tongtong"; // docs default :contentReference[oaicite:1]{index=1}

        var responseFormat =
            (request.OutputFormat ?? metadata?.Glm?.ResponseFormat ?? "pcm").ToLowerInvariant();

        // GLM endpoint only supports wav|pcm :contentReference[oaicite:2]{index=2}
        if (responseFormat is not ("wav" or "pcm"))
            responseFormat = "pcm";

        var speed = request.Speed ?? metadata?.Glm?.Speed ?? 1.0;
        if (speed < 0.5) speed = 0.5;
        if (speed > 2.0) speed = 2.0; // docs range :contentReference[oaicite:3]{index=3}

        var volume = metadata?.Glm?.Volume ?? 1.0;
        if (volume <= 0) volume = 1.0;
        if (volume > 10) volume = 10; // docs range :contentReference[oaicite:4]{index=4}

        var payload = new Dictionary<string, object?>
        {
            ["input"] = request.Text ?? "",
            ["voice"] = voice,
            ["speed"] = speed,
            ["volume"] = volume,
            ["response_format"] = responseFormat,
            ["watermark_enabled"] = metadata?.Glm?.WatermarkEnabled
        };

        using var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json"
        );

        using var resp = await _client.PostAsync(BaseUrl + request.Model, content, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Novita GLM-TTS failed ({(int)resp.StatusCode}): {err}"
            );
        }

        // Response is binary audio :contentReference[oaicite:5]{index=5}
        var warnings = new List<object>();

        // For browser playback: prefer wav. PCM is raw; many players won’t like a data-url of it.
        var mime = responseFormat == "wav"
            ? "audio/wav"
            : "application/octet-stream";

        if (responseFormat == "pcm")
            warnings.Add("GLM-TTS returned pcm (raw). If you want easy browser playback, request response_format=wav.");

        var base64 = Convert
            .ToBase64String(bytes)
            .ToDataUrl(mime);

        return new SpeechResponse
        {
            Audio = base64,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model ?? "glm-tts",
            }
        };
    }

    private static bool IsGlmTtsModel(string? model)
           => string.Equals(model, "glm-tts", StringComparison.OrdinalIgnoreCase);
    
}