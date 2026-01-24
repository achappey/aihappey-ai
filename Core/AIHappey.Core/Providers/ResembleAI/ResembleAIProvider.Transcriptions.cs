using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.ResembleAI;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        // Model: accept either provider-prefixed or raw.
        // Resemble STT does not require a model field; we only keep it for tracing.
        var modelId = string.IsNullOrWhiteSpace(request.Model)
            ? "speech-to-text"
            : request.Model;

        var metadata = request.GetProviderMetadata<ResembleAITranscriptionProviderMetadata>(GetIdentifier());

        // Unified request can be base64 or data-url.
        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        var now = DateTime.UtcNow;

        // 1) Create transcript job (multipart form)
        var jobUuid = await CreateTranscriptJobAsync(bytes, request.MediaType, metadata, cancellationToken);

        // 2) Poll until completed/failed
        var completedJson = await PollTranscriptUntilDoneAsync(jobUuid, cancellationToken);

        // 3) Convert
        return ConvertTranscriptResponse(completedJson, modelId, now);
    }

    private async Task<string> CreateTranscriptJobAsync(
        byte[] bytes,
        string mediaType,
        ResembleAITranscriptionProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + mediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        form.Add(file, "file", fileName);

        if (!string.IsNullOrWhiteSpace(metadata?.Query))
            form.Add(new StringContent(metadata.Query), "query");

        using var resp = await _client.PostAsync("api/v2/speech-to-text", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ResembleAI STT create job failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // { success: true, item: { uuid: "..." } }
        var uuid = root.TryGetProperty("item", out var item)
                   && item.ValueKind == JsonValueKind.Object
                   && item.TryGetProperty("uuid", out var u)
                   && u.ValueKind == JsonValueKind.String
            ? (u.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(uuid))
            throw new InvalidOperationException($"ResembleAI STT create job response did not contain item.uuid. Body: {json}");

        return uuid;
    }

    private async Task<string> PollTranscriptUntilDoneAsync(string uuid, CancellationToken cancellationToken)
    {
        // Bounded backoff: start 1s, cap 5s, max wait 10m.
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var maxWait = TimeSpan.FromMinutes(10);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync($"api/v2/speech-to-text/{Uri.EscapeDataString(uuid)}", cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"ResembleAI STT get transcript failed ({(int)resp.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // { success: true, item: { status: "pending"|"processing"|"completed"|"failed", ... } }
            var status = root.TryGetProperty("item", out var item)
                         && item.ValueKind == JsonValueKind.Object
                         && item.TryGetProperty("status", out var s)
                         && s.ValueKind == JsonValueKind.String
                ? (s.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return json;

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ResembleAI STT transcript failed. Body: {json}");

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"ResembleAI STT transcript did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            delay = delay < maxDelay
                ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5))
                : maxDelay;
        }
    }

    private static TranscriptionResponse ConvertTranscriptResponse(string json, string modelId, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return new TranscriptionResponse
            {
                Text = string.Empty,
                Segments = [],
                Warnings = [new { type = "unexpected_response_shape", provider = "resembleai" }],
                Response = new() { Timestamp = timestamp, ModelId = modelId, Body = json }
            };
        }

        var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? (t.GetString() ?? string.Empty)
            : string.Empty;

        var durationSeconds = item.TryGetProperty("duration_seconds", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? (float?)dur.GetDouble()
            : null;

        var segments = new List<TranscriptionSegment>();
        if (item.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in words.EnumerateArray())
            {
                var wText = w.TryGetProperty("text", out var wt) && wt.ValueKind == JsonValueKind.String
                    ? (wt.GetString() ?? string.Empty)
                    : string.Empty;

                var start = w.TryGetProperty("start_time", out var ws) && ws.ValueKind == JsonValueKind.Number
                    ? (float)ws.GetDouble()
                    : 0f;

                var end = w.TryGetProperty("end_time", out var we) && we.ValueKind == JsonValueKind.Number
                    ? (float)we.GetDouble()
                    : start;

                if (w.TryGetProperty("speaker_id", out var sp) && sp.ValueKind == JsonValueKind.String)
                {
                    var speakerId = sp.GetString();
                    if (!string.IsNullOrWhiteSpace(speakerId))
                        wText = $"{speakerId}: {wText}";
                }

                if (!string.IsNullOrWhiteSpace(wText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = wText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        return new TranscriptionResponse
        {
            ProviderMetadata = null,
            Text = text,
            Language = null,
            DurationInSeconds = durationSeconds,
            Segments = segments,
            Warnings = [],
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = modelId,
                Body = json
            }
        };
    }
}

