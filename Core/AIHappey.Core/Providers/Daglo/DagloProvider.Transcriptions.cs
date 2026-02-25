using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Daglo;

public partial class DagloProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var warnings = new List<object>();

        var audioBase64 = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioBase64, out _, out var parsedBase64))
            audioBase64 = parsedBase64;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audioBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        var effectiveModel = ResolveModel(request.Model, metadata);
        var language = TryGetDagloLanguage(metadata);
        var maxWait = ResolvePollingTimeout(metadata);

        var rid = await SubmitAsyncTranscriptionAsync(
            bytes,
            request.MediaType,
            effectiveModel,
            language,
            cancellationToken);

        var completedJson = await PollUntilCompletedAsync(rid, maxWait, cancellationToken);

        return ConvertTranscriptionResponse(
            completedJson,
            rid,
            effectiveModel,
            now,
            warnings,
            language);
    }

    private async Task<string> SubmitAsyncTranscriptionAsync(
        byte[] bytes,
        string mediaType,
        string model,
        string? language,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + mediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        form.Add(file, "file", fileName);

        var sttConfig = new Dictionary<string, object?>
        {
            ["model"] = model
        };

        if (!string.IsNullOrWhiteSpace(language))
            sttConfig["language"] = language;

        form.Add(new StringContent(JsonSerializer.Serialize(sttConfig), Encoding.UTF8, "application/json"), "sttConfig");

        using var resp = await _client.PostAsync("stt/v1/async/transcripts", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Daglo STT init failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var rid = root.TryGetProperty("rid", out var ridEl) && ridEl.ValueKind == JsonValueKind.String
            ? ridEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(rid))
            throw new InvalidOperationException($"Daglo STT init response did not contain rid. Body: {json}");

        return rid;
    }

    private async Task<string> PollUntilCompletedAsync(string rid, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(6);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync($"stt/v1/async/transcripts/{Uri.EscapeDataString(rid)}", cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if ((int)resp.StatusCode == 204)
                return "{}";

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Daglo STT status failed ({(int)resp.StatusCode}): {body}");

            var status = string.Empty;
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                    ? statusEl.GetString() ?? string.Empty
                    : string.Empty;

                if (string.Equals(status, "transcribed", StringComparison.OrdinalIgnoreCase))
                    return body;

                if (string.Equals(status, "input_error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "transcript_error", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, "file_error", StringComparison.OrdinalIgnoreCase))
                {
                    var message = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                        ? msgEl.GetString()
                        : null;

                    throw new InvalidOperationException($"Daglo STT failed with status '{status}'. {message}");
                }
            }

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"Daglo STT did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            if (delay < maxDelay)
                delay = TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5));
        }
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string rid,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        string? requestedLanguage)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            return new TranscriptionResponse
            {
                Text = string.Empty,
                Language = requestedLanguage,
                Segments = [],
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = timestamp,
                    ModelId = model,
                    Body = json
                }
            };
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transcripts = new List<string>();
        var segments = new List<TranscriptionSegment>();
        var duration = 0f;

        if (root.TryGetProperty("sttResults", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in resultsEl.EnumerateArray())
            {
                var transcript = result.TryGetProperty("transcript", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? string.Empty)
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(transcript))
                    transcripts.Add(transcript);

                if (result.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var word in wordsEl.EnumerateArray())
                    {
                        var wordText = word.TryGetProperty("word", out var wEl) && wEl.ValueKind == JsonValueKind.String
                            ? (wEl.GetString() ?? string.Empty)
                            : string.Empty;

                        var start = ParseTimeSeconds(word, "startTime");
                        var end = ParseTimeSeconds(word, "endTime");
                        if (end < start)
                            end = start;

                        duration = Math.Max(duration, end);

                        if (!string.IsNullOrWhiteSpace(wordText))
                        {
                            segments.Add(new TranscriptionSegment
                            {
                                Text = wordText,
                                StartSecond = start,
                                EndSecond = end
                            });
                        }
                    }
                }
            }
        }

        var text = transcripts.Count > 0
            ? string.Join(" ", transcripts)
            : string.Empty;

        return new TranscriptionResponse
        {
            Text = text,
            Language = requestedLanguage,
            DurationInSeconds = duration > 0 ? duration : null,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [nameof(Daglo).ToLowerInvariant()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = model,
                Body = new { rid, status = "transcribed", raw = json }
            }
        };
    }

    private static float ParseTimeSeconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var timeEl) || timeEl.ValueKind != JsonValueKind.Object)
            return 0f;

        var seconds = 0d;
        var nanos = 0d;

        if (timeEl.TryGetProperty("seconds", out var secEl))
        {
            if (secEl.ValueKind == JsonValueKind.String)
                double.TryParse(secEl.GetString(), out seconds);
            else if (secEl.ValueKind == JsonValueKind.Number)
                seconds = secEl.GetDouble();
        }

        if (timeEl.TryGetProperty("nanos", out var nanosEl) && nanosEl.ValueKind == JsonValueKind.Number)
            nanos = nanosEl.GetDouble();

        return (float)(seconds + (nanos / 1_000_000_000d));
    }

    private static string ResolveModel(string? model, JsonElement? metadata)
    {
        if (!string.IsNullOrWhiteSpace(model))
            return model;

        var fromMetadata = TryReadString(metadata, "model")
            ?? TryReadNestedString(metadata, "sttConfig", "model");

        return string.IsNullOrWhiteSpace(fromMetadata) ? "general" : fromMetadata;
    }

    private static string? TryGetDagloLanguage(JsonElement? metadata)
        => TryReadString(metadata, "language")
           ?? TryReadNestedString(metadata, "sttConfig", "language");

    private static TimeSpan ResolvePollingTimeout(JsonElement? metadata)
    {
        var timeoutSeconds = TryReadInt(metadata, "pollTimeoutSeconds")
            ?? TryReadNestedInt(metadata, "polling", "timeoutSeconds")
            ?? 1800;

        timeoutSeconds = Math.Clamp(timeoutSeconds, 30, 14_400);
        return TimeSpan.FromSeconds(timeoutSeconds);
    }

    private static string? TryReadString(JsonElement? metadata, string propertyName)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.Value.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        return el.GetString();
    }

    private static string? TryReadNestedString(JsonElement? metadata, string parentProperty, string propertyName)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.Value.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;

        if (!parent.TryGetProperty(propertyName, out var child) || child.ValueKind != JsonValueKind.String)
            return null;

        return child.GetString();
    }

    private static int? TryReadInt(JsonElement? metadata, string propertyName)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.Value.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;

        return el.TryGetInt32(out var value) ? value : null;
    }

    private static int? TryReadNestedInt(JsonElement? metadata, string parentProperty, string propertyName)
    {
        if (metadata is null || metadata.Value.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.Value.TryGetProperty(parentProperty, out var parent) || parent.ValueKind != JsonValueKind.Object)
            return null;

        if (!parent.TryGetProperty(propertyName, out var child) || child.ValueKind != JsonValueKind.Number)
            return null;

        return child.TryGetInt32(out var value) ? value : null;
    }
}
