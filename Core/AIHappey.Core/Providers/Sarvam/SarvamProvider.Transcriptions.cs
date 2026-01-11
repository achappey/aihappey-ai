using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Sarvam;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;

namespace AIHappey.Core.Providers.Sarvam;

public partial class SarvamProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        // Model: strip provider prefix if present.
        var model = request.Model;
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
        {
            var split = model.SplitModelId();
            model = string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
                ? split.Model
                : request.Model;
        }

        if (string.IsNullOrWhiteSpace(model))
            model = "saarika:v2.5";

        var now = DateTime.UtcNow;

        var metadata = request.GetTranscriptionProviderMetadata<SarvamTranscriptionProviderMetadata>(GetIdentifier());

        // Sarvam expects raw bytes; unified request can be base64 or data-url.
        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);

        // File name: derive extension from media type (fallback to .wav).
        var fileName = "audio.wav";
        if (!string.IsNullOrWhiteSpace(request.MediaType))
        {
            try
            {
                fileName = "audio" + request.MediaType.GetAudioExtension();
            }
            catch (NotSupportedException)
            {
                fileName = "audio.wav";
            }
        }

        // Default: timestamps ON (per product decision), but allow override.
        var withTimestamps = metadata?.WithTimestamps ?? true;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(metadata?.Model ?? model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.LanguageCode))
            form.Add(new StringContent(metadata.LanguageCode), "language_code");

        if (!string.IsNullOrWhiteSpace(metadata?.InputAudioCodec))
            form.Add(new StringContent(metadata.InputAudioCodec), "input_audio_codec");

        form.Add(new StringContent(withTimestamps ? "true" : "false"), "with_timestamps");

        using var resp = await _client.PostAsync("speech-to-text", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sarvam STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertSarvamTranscriptionResponse(json, metadata?.Model ?? model, now);
    }

    private static TranscriptionResponse ConvertSarvamTranscriptionResponse(
        string json,
        string model,
        DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcript = root.TryGetProperty("transcript", out var transcriptEl)
            ? transcriptEl.GetString() ?? ""
            : "";

        var language = root.TryGetProperty("language_code", out var languageEl)
            ? languageEl.GetString()
            : null;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("timestamps", out var timestampsEl) &&
            timestampsEl.ValueKind == JsonValueKind.Object &&
            timestampsEl.TryGetProperty("words", out var wordsEl) &&
            timestampsEl.TryGetProperty("start_time_seconds", out var startEl) &&
            timestampsEl.TryGetProperty("end_time_seconds", out var endEl) &&
            wordsEl.ValueKind == JsonValueKind.Array &&
            startEl.ValueKind == JsonValueKind.Array &&
            endEl.ValueKind == JsonValueKind.Array)
        {
            var words = wordsEl.EnumerateArray().Select(w => w.GetString() ?? "").ToList();
            var starts = startEl.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0d).ToList();
            var ends = endEl.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0d).ToList();

            var n = new[] { words.Count, starts.Count, ends.Count }.Min();
            for (var i = 0; i < n; i++)
            {
                // Per product decision: expose per-word segments.
                segments.Add(new TranscriptionSegment
                {
                    Text = words[i],
                    StartSecond = (float)starts[i],
                    EndSecond = (float)ends[i]
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = transcript,
            Language = language,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = model,
                Body = json
            }
        };
    }
}

