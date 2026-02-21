using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.ARKLabs;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ARKLabs;

public partial class ARKLabsProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<ARKLabsTranscriptionProviderMetadata>(GetIdentifier());

        var model = request.Model;
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
        {
            var split = model.SplitModelId();
            model = split.Model;
        }

        if (string.IsNullOrWhiteSpace(model))
            model = "whisper-1";

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

        var responseFormat = string.Equals(metadata?.ResponseFormat, "srt", StringComparison.OrdinalIgnoreCase)
            ? "srt"
            : "json";

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(responseFormat), "response_format");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
            form.Add(new StringContent(metadata.Prompt), "prompt");

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"ARKLabs transcription failed ({(int)resp.StatusCode}): {body}");

        if (string.Equals(responseFormat, "srt", StringComparison.OrdinalIgnoreCase))
            return ConvertSrtResponse(body, request.Model, now);

        return ConvertJsonResponse(body, request.Model, now);
    }

    private static TranscriptionResponse ConvertJsonResponse(string json, string modelId, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                segments.Add(new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? string.Empty : string.Empty,
                    StartSecond = seg.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.Number
                        ? (float)startEl.GetDouble()
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.Number
                        ? (float)endEl.GetDouble()
                        : 0f,
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var text)
                ? text.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(a => a.Text)),
            Language = root.TryGetProperty("language", out var language)
                ? language.GetString()
                : null,
            DurationInSeconds = root.TryGetProperty("duration", out var duration) && duration.ValueKind == JsonValueKind.Number
                ? (float)duration.GetDouble()
                : null,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = modelId,
                Body = json
            }
        };
    }

    private static TranscriptionResponse ConvertSrtResponse(string srt, string modelId, DateTime timestamp)
    {
        var segments = ParseSrtSegments(srt);

        return new TranscriptionResponse
        {
            Text = srt,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = modelId,
                Body = srt
            }
        };
    }

    private static List<TranscriptionSegment> ParseSrtSegments(string srt)
    {
        var segments = new List<TranscriptionSegment>();

        if (string.IsNullOrWhiteSpace(srt))
            return segments;

        var blocks = Regex.Split(srt.Trim(), "\\r?\\n\\s*\\r?\\n");
        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block))
                continue;

            var lines = block.Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToList();

            if (lines.Count < 2)
                continue;

            var timelineLineIndex = lines.FindIndex(a => a.Contains("-->", StringComparison.Ordinal));
            if (timelineLineIndex < 0)
                continue;

            var timeline = lines[timelineLineIndex].Split("-->", StringSplitOptions.TrimEntries);
            if (timeline.Length != 2)
                continue;

            if (!TryParseSrtTime(timeline[0], out var startSecond) || !TryParseSrtTime(timeline[1], out var endSecond))
                continue;

            var textLines = lines.Skip(timelineLineIndex + 1);
            var text = string.Join("\n", textLines).Trim();

            if (string.IsNullOrWhiteSpace(text))
                continue;

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = startSecond,
                EndSecond = endSecond
            });
        }

        return segments;
    }

    private static bool TryParseSrtTime(string value, out float seconds)
    {
        seconds = 0f;

        var sanitized = value.Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            return false;

        var parsed = TimeSpan.TryParseExact(
            sanitized,
            [@"hh\:mm\:ss\,fff", @"hh\:mm\:ss\.fff", @"h\:mm\:ss\,fff", @"h\:mm\:ss\.fff"],
            CultureInfo.InvariantCulture,
            out var ts);

        if (!parsed)
            return false;

        seconds = (float)ts.TotalSeconds;
        return true;
    }
}

