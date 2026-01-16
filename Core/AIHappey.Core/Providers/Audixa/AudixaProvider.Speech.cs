using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Audixa;

namespace AIHappey.Core.Providers.Audixa;

public partial class AudixaProvider
{

    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Model.Contains('/'))
            throw new ArgumentException("Audixa model must be 'base' or 'advance' (no provider prefix).", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var metadata = request.GetSpeechProviderMetadata<AudixaSpeechProviderMetadata>(GetIdentifier());

        var voice = (request.Voice ?? metadata?.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Audixa requires a voice. Provide SpeechRequest.voice or providerOptions.audixa.voice.", nameof(request));

        var speed = request.Speed ?? metadata?.Speed;
        var emotion = metadata?.Emotion;
        var temperature = metadata?.Temperature;
        var topP = metadata?.TopP;

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice"] = voice,
            ["model"] = request.Model,
        };

        if (speed is not null)
            payload["speed"] = speed;

        if (!string.IsNullOrEmpty(emotion))
            payload["emotion"] = emotion;

        if (temperature is not null)
            payload["temperature"] = temperature;

        if (topP is not null)
            payload["top_p"] = topP;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/tts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Audixa TTS failed ({(int)resp.StatusCode}): {body}");

        using var createDoc = JsonDocument.Parse(body);
        var generationId = createDoc.RootElement.TryGetProperty("generation_id", out var genEl)
            ? genEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(generationId))
            throw new InvalidOperationException("Audixa TTS returned no generation_id.");

        var start = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(60);
        JsonElement? statusRoot = null;
        string? audioUrl = null;

        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            using var pollResp = await _client.GetAsync(
                $"v2/status?generation_id={Uri.EscapeDataString(generationId)}",
                cancellationToken);

            var pollJson = await pollResp.Content.ReadAsStringAsync(cancellationToken);

            if (!pollResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Audixa TTS status failed ({(int)pollResp.StatusCode}): {pollJson}");

            using var pollDoc = JsonDocument.Parse(pollJson);
            statusRoot = pollDoc.RootElement.Clone();

            var status = pollDoc.RootElement.TryGetProperty("status", out var statusEl)
                ? statusEl.GetString()
                : null;

            if (string.Equals(status, "Generating", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                var detail = pollDoc.RootElement.TryGetProperty("detail", out var detailEl)
                    ? detailEl.GetString()
                    : "Unknown error";
                throw new InvalidOperationException($"Audixa TTS failed: {detail}");
            }

            if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                audioUrl = pollDoc.RootElement.TryGetProperty("url", out var urlEl)
                    ? urlEl.GetString()
                    : null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            throw new TimeoutException(
                "Audixa TTS timed out waiting for completion.");
        }

        var bytes = await _client.GetByteArrayAsync(audioUrl, cancellationToken);
        var base64 = Convert.ToBase64String(bytes);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["generation_id"] = JsonSerializer.SerializeToElement(generationId, JsonSerializerOptions.Web),
            ["model"] = JsonSerializer.SerializeToElement(request.Model, JsonSerializerOptions.Web),
            ["voice"] = JsonSerializer.SerializeToElement(voice, JsonSerializerOptions.Web)
        };

        if (speed is not null)
            providerMetadata["speed"] = JsonSerializer.SerializeToElement(speed, JsonSerializerOptions.Web);
        if (!string.IsNullOrWhiteSpace(emotion))
            providerMetadata["emotion"] = JsonSerializer.SerializeToElement(emotion, JsonSerializerOptions.Web);
        if (temperature is not null)
            providerMetadata["temperature"] = JsonSerializer.SerializeToElement(temperature, JsonSerializerOptions.Web);
        if (topP is not null)
            providerMetadata["top_p"] = JsonSerializer.SerializeToElement(topP, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = "audio/mpeg",
                Format = "mp3"
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    create = createDoc.RootElement.Clone(),
                    status = statusRoot
                }
            }
        };
    }
}

