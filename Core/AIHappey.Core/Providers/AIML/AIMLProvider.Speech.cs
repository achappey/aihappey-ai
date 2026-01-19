using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Text;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.AIML;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Unified fields that do not map cleanly to AIML music/audio generation.
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions", detail = "Use providerOptions.aiml.lyrics instead" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var metadata = request.GetSpeechProviderMetadata<AIMLSpeechProviderMetadata>(GetIdentifier());

        // Build model-aware payload (lyrics only for minimax/music-2.0 etc.).
        var payload = request.GetAudioRequestPayload(metadata, warnings);

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        // 1) Create generation task
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v2/generate/audio")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createBody = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIML audio create failed ({(int)createResp.StatusCode}): {createBody}");

        using var createDoc = JsonDocument.Parse(createBody);
        var genId = createDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(genId))
            throw new InvalidOperationException($"AIML audio create returned no id. Body: {createBody}");

        // 2) Poll
        var timeout = TimeSpan.FromMinutes(10);
        var start = DateTime.UtcNow;
        var pollInterval = TimeSpan.FromSeconds(10);

        JsonElement? lastPollRoot = null;
        string? lastPollJson = null;

        while (DateTime.UtcNow - start < timeout)
        {
            await Task.Delay(pollInterval, cancellationToken);

            var pollUrl = $"v2/generate/audio?generation_id={Uri.EscapeDataString(genId)}";
            using var pollResp = await _client.GetAsync(pollUrl, cancellationToken);
            lastPollJson = await pollResp.Content.ReadAsStringAsync(cancellationToken);
            if (!pollResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AIML audio poll failed ({(int)pollResp.StatusCode}): {lastPollJson}");

            using var pollDoc = JsonDocument.Parse(lastPollJson);
            var root = pollDoc.RootElement;
            lastPollRoot = root;

            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;

            if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "generating", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var errMsg = "Unknown error";
                if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind != JsonValueKind.Null)
                    errMsg = errEl.ToString();
                throw new InvalidOperationException($"AIML audio generation did not complete (status={status}). Error: {errMsg}. Body: {lastPollJson}");
            }

            // 3) Completed: fetch audio
            if (!root.TryGetProperty("audio_file", out var audioFileEl) || audioFileEl.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"AIML audio completed but returned no audio_file. Body: {lastPollJson}");

            var audioUrl = audioFileEl.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(audioUrl))
                throw new InvalidOperationException($"AIML audio completed but returned empty audio_file.url. Body: {lastPollJson}");

            // Download bytes from CDN URL.
            using var audioReq = new HttpRequestMessage(HttpMethod.Get, audioUrl);
            using var audioResp = await _client.SendAsync(audioReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var bytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!audioResp.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"AIML audio download failed ({(int)audioResp.StatusCode}): {text}");
            }

            var contentType = audioResp.Content.Headers.ContentType?.MediaType;
            var (mime, format) = InferAudioType(audioFileEl, audioUrl, contentType);

            return new SpeechResponse
            {
                Audio = new()
                {
                    Base64 = Convert.ToBase64String(bytes),
                    MimeType = mime,
                    Format = format
                },
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = now,
                    ModelId = request.Model,
                    Body = lastPollJson
                }
            };
        }

        throw new TimeoutException("AIML audio generation timed out");
    }

    private static (string MimeType, string Format) InferAudioType(JsonElement audioFileEl, string audioUrl, string? httpContentType)
    {
        // Prefer content_type from payload if present.
        if (audioFileEl.TryGetProperty("content_type", out var ctEl))
        {
            var ct = ctEl.GetString();
            if (!string.IsNullOrWhiteSpace(ct))
            {
                var fmt = ct.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3"
                    : ct.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "wav"
                    : "bin";
                return (ct, fmt);
            }
        }

        // Then use HTTP content-type.
        if (!string.IsNullOrWhiteSpace(httpContentType))
        {
            var fmt = httpContentType.Contains("mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3"
                : httpContentType.Contains("wav", StringComparison.OrdinalIgnoreCase) ? "wav"
                : "bin";
            return (httpContentType, fmt);
        }

        // Finally infer from URL extension.
        var ext = Path.GetExtension(audioUrl)?.Trim('.').ToLowerInvariant();
        return ext switch
        {
            "mp3" => ("audio/mpeg", "mp3"),
            "wav" => ("audio/wav", "wav"),
            _ => ("application/octet-stream", ext is null or "" ? "bin" : ext)
        };
    }

}
