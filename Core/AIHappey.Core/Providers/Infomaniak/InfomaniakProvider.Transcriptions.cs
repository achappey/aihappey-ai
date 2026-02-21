using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Infomaniak;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var metadata = request.GetProviderMetadata<InfomaniakTranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        _ = Convert.FromBase64String(audioString);

        var productId = await GetProductIdAsync(cancellationToken);
        var batchId = await CreateTranscriptionBatchAsync(productId, request.Model, audioString, metadata, cancellationToken);
        var resultRoot = await PollBatchResultUntilDoneAsync(productId, batchId, cancellationToken);

        return ConvertTranscriptionResult(resultRoot, request.Model, now, warnings);
    }

    private async Task<string> CreateTranscriptionBatchAsync(
        int productId,
        string model,
        string audioBase64,
        InfomaniakTranscriptionProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["file"] = audioBase64,
            ["model"] = model
        };

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            payload["language"] = metadata.Language;

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            payload["prompt"] = metadata.Prompt;

        if (!string.IsNullOrWhiteSpace(metadata?.ResponseFormat))
            payload["response_format"] = metadata.ResponseFormat;

        if (metadata?.TimestampGranularities?.Any() == true)
            payload["timestamp_granularities"] = metadata.TimestampGranularities.ToArray();

        if (!string.IsNullOrWhiteSpace(metadata?.AppendPunctuations))
            payload["append_punctuations"] = metadata.AppendPunctuations;

        if (!string.IsNullOrWhiteSpace(metadata?.PrependPunctuations))
            payload["prepend_punctuations"] = metadata.PrependPunctuations;

        if (metadata?.ChunkLength is not null)
            payload["chunk_length"] = metadata.ChunkLength.Value;

        if (metadata?.HighlightWords is not null)
            payload["highlight_words"] = metadata.HighlightWords.Value;

        if (metadata?.MaxLineCount is not null)
            payload["max_line_count"] = metadata.MaxLineCount.Value;

        if (metadata?.MaxLineWidth is not null)
            payload["max_line_width"] = metadata.MaxLineWidth.Value;

        if (metadata?.MaxWordsPerLine is not null)
            payload["max_words_per_line"] = metadata.MaxWordsPerLine.Value;

        if (metadata?.NoSpeechThreshold is not null)
            payload["no_speech_threshold"] = metadata.NoSpeechThreshold.Value;

        var reqJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"1/ai/{productId}/openai/audio/transcriptions")
        {
            Content = new StringContent(reqJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Infomaniak transcription create failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var batchId = root.TryGetProperty("batch_id", out var batchIdEl) && batchIdEl.ValueKind == JsonValueKind.String
            ? (batchIdEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(batchId))
            throw new InvalidOperationException($"Infomaniak transcription create response missing batch_id. Body: {raw}");

        return batchId;
    }

    private async Task<JsonElement> PollBatchResultUntilDoneAsync(int productId, string batchId, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var maxWait = TimeSpan.FromMinutes(10);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, $"1/ai/{productId}/results/{Uri.EscapeDataString(batchId)}");
            using var resp = await _client.SendAsync(req, cancellationToken);
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Infomaniak transcription poll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? (statusEl.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return root.Clone();

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Infomaniak transcription batch ended with status '{status}'. Body: {raw}");
            }

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"Infomaniak transcription did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            delay = delay < maxDelay
                ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5))
                : maxDelay;
        }
    }

    private static TranscriptionResponse ConvertTranscriptionResult(
        JsonElement batchResultRoot,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        if (!batchResultRoot.TryGetProperty("data", out var dataEl))
            throw new InvalidOperationException("Infomaniak transcription result does not contain 'data'.");

        if (TryParseStructuredTranscription(dataEl, out var structured))
        {
            return new TranscriptionResponse
            {
                Text = structured.Text,
                Language = structured.Language,
                DurationInSeconds = structured.DurationInSeconds,
                Segments = structured.Segments,
                Warnings = warnings,
                Response = new ResponseData
                {
                    Timestamp = now,
                    ModelId = model,
                    Body = batchResultRoot.Clone()
                }
            };
        }

        var plainText = dataEl.ValueKind == JsonValueKind.String
            ? (dataEl.GetString() ?? string.Empty)
            : dataEl.GetRawText();

        return new TranscriptionResponse
        {
            Text = plainText,
            Language = null,
            DurationInSeconds = null,
            Segments = [],
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = batchResultRoot.Clone()
            }
        };
    }

    private static bool TryParseStructuredTranscription(JsonElement dataEl, out (string Text, string? Language, float? DurationInSeconds, List<TranscriptionSegment> Segments) result)
    {
        result = (string.Empty, null, null, []);

        JsonElement root;
        JsonDocument? parsedDoc = null;
        try
        {
            if (dataEl.ValueKind == JsonValueKind.Object)
            {
                root = dataEl;
            }
            else if (dataEl.ValueKind == JsonValueKind.String)
            {
                var str = dataEl.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return false;

                str = str.Trim();
                if (!(str.StartsWith('{') && str.EndsWith('}')))
                    return false;

                parsedDoc = JsonDocument.Parse(str);
                root = parsedDoc.RootElement;
            }
            else
            {
                return false;
            }

            var segments = new List<TranscriptionSegment>();

            if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in segmentsEl.EnumerateArray())
                {
                    var segmentText = seg.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                        ? (textEl.GetString() ?? string.Empty)
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(segmentText))
                        continue;

                    var start = seg.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.Number
                        ? (float)startEl.GetDouble()
                        : 0f;

                    var end = seg.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.Number
                        ? (float)endEl.GetDouble()
                        : start;

                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }

            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString() ?? string.Empty)
                : string.Join(" ", segments.Select(a => a.Text));

            var language = root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                ? lang.GetString()
                : null;

            float? duration = null;
            if (root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                duration = (float)dur.GetDouble();
            }
            else if (root.TryGetProperty("duration_in_seconds", out var durS) && durS.ValueKind == JsonValueKind.Number)
            {
                duration = (float)durS.GetDouble();
            }

            result = (text, language, duration, segments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            parsedDoc?.Dispose();
        }
    }
}

