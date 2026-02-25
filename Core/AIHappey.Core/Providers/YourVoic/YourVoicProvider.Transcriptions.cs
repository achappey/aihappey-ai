using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.YourVoic;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.YourVoic;

public partial class YourVoicProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

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
        var metadata = request.GetProviderMetadata<YourVoicTranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var model = request.Model.Trim();

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(model), "model");

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            form.Add(new StringContent(metadata.Language), "language");

        var isCipher = model.StartsWith("cipher-", StringComparison.OrdinalIgnoreCase);
        var isLucid = model.StartsWith("lucid-", StringComparison.OrdinalIgnoreCase);

        if (isCipher)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.ResponseFormat))
                form.Add(new StringContent(metadata.ResponseFormat), "response_format");

            if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
                form.Add(new StringContent(metadata.Prompt), "prompt");

            if (!string.IsNullOrWhiteSpace(metadata?.TimestampGranularities))
                form.Add(new StringContent(metadata.TimestampGranularities), "timestamp_granularities");

            if (metadata?.Diarize is not null)
                warnings.Add(new { type = "ignored", feature = "diarize", reason = "Only supported for lucid-* models." });
            if (metadata?.SmartFormat is not null)
                warnings.Add(new { type = "ignored", feature = "smart_format", reason = "Only supported for lucid-* models." });
            if (metadata?.Punctuate is not null)
                warnings.Add(new { type = "ignored", feature = "punctuate", reason = "Only supported for lucid-* models." });
            if (!string.IsNullOrWhiteSpace(metadata?.Keywords))
                warnings.Add(new { type = "ignored", feature = "keywords", reason = "Only supported for lucid-* models." });
        }
        else if (isLucid)
        {
            if (metadata?.Diarize is not null)
                form.Add(new StringContent(metadata.Diarize.Value.ToString().ToLowerInvariant()), "diarize");

            if (metadata?.SmartFormat is not null)
                form.Add(new StringContent(metadata.SmartFormat.Value.ToString().ToLowerInvariant()), "smart_format");

            if (metadata?.Punctuate is not null)
                form.Add(new StringContent(metadata.Punctuate.Value.ToString().ToLowerInvariant()), "punctuate");

            if (!string.IsNullOrWhiteSpace(metadata?.Keywords))
                form.Add(new StringContent(metadata.Keywords), "keywords");

            if (!string.IsNullOrWhiteSpace(metadata?.ResponseFormat))
                warnings.Add(new { type = "ignored", feature = "response_format", reason = "Only supported for cipher-* models." });
            if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
                warnings.Add(new { type = "ignored", feature = "prompt", reason = "Only supported for cipher-* models." });
            if (!string.IsNullOrWhiteSpace(metadata?.TimestampGranularities))
                warnings.Add(new { type = "ignored", feature = "timestamp_granularities", reason = "Only supported for cipher-* models." });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(metadata?.ResponseFormat))
                form.Add(new StringContent(metadata.ResponseFormat), "response_format");

            if (!string.IsNullOrWhiteSpace(metadata?.Prompt))
                form.Add(new StringContent(metadata.Prompt), "prompt");

            if (!string.IsNullOrWhiteSpace(metadata?.TimestampGranularities))
                form.Add(new StringContent(metadata.TimestampGranularities), "timestamp_granularities");

            if (metadata?.Diarize is not null)
                form.Add(new StringContent(metadata.Diarize.Value.ToString().ToLowerInvariant()), "diarize");

            if (metadata?.SmartFormat is not null)
                form.Add(new StringContent(metadata.SmartFormat.Value.ToString().ToLowerInvariant()), "smart_format");

            if (metadata?.Punctuate is not null)
                form.Add(new StringContent(metadata.Punctuate.Value.ToString().ToLowerInvariant()), "punctuate");

            if (!string.IsNullOrWhiteSpace(metadata?.Keywords))
                form.Add(new StringContent(metadata.Keywords), "keywords");
        }

        var endpoint = isCipher
            ? "stt/cipher/transcribe"
            : isLucid
                ? "stt/lucid/transcribe"
                : "stt/transcribe";

        using var resp = await _client.PostAsync(endpoint, form, cancellationToken);
        var responseText = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"YourVoic STT failed ({(int)resp.StatusCode}): {responseText}");

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        return ConvertTranscriptionResponse(responseText, mediaType, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string responseBody,
        string? contentType,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

            var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
                ? langEl.GetString()
                : null;

            float? duration = null;
            if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                duration = (float)durEl.GetDouble();

            var segments = new List<TranscriptionSegment>();

            if (root.TryGetProperty("utterances", out var utterancesEl) && utterancesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var utterance in utterancesEl.EnumerateArray())
                {
                    var segmentText = utterance.TryGetProperty("text", out var ut) && ut.ValueKind == JsonValueKind.String
                        ? ut.GetString() ?? string.Empty
                        : string.Empty;

                    if (utterance.TryGetProperty("speaker", out var speakerEl))
                    {
                        var speaker = speakerEl.ValueKind == JsonValueKind.String
                            ? speakerEl.GetString()
                            : speakerEl.ValueKind == JsonValueKind.Number
                                ? speakerEl.GetInt32().ToString()
                                : null;

                        if (!string.IsNullOrWhiteSpace(speaker) && !string.IsNullOrWhiteSpace(segmentText))
                            segmentText = $"speaker_{speaker}: {segmentText}";
                    }

                    var start = utterance.TryGetProperty("start", out var us) && us.ValueKind == JsonValueKind.Number
                        ? (float)us.GetDouble()
                        : 0f;

                    var end = utterance.TryGetProperty("end", out var ue) && ue.ValueKind == JsonValueKind.Number
                        ? (float)ue.GetDouble()
                        : start;

                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new TranscriptionSegment
                        {
                            Text = segmentText,
                            StartSecond = start,
                            EndSecond = end
                        });
                    }
                }
            }
            else if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsEl.EnumerateArray())
                {
                    var segmentText = segment.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String
                        ? st.GetString() ?? string.Empty
                        : string.Empty;

                    var start = segment.TryGetProperty("start", out var ss) && ss.ValueKind == JsonValueKind.Number
                        ? (float)ss.GetDouble()
                        : 0f;

                    var end = segment.TryGetProperty("end", out var se) && se.ValueKind == JsonValueKind.Number
                        ? (float)se.GetDouble()
                        : start;

                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new TranscriptionSegment
                        {
                            Text = segmentText,
                            StartSecond = start,
                            EndSecond = end
                        });
                    }
                }
            }
            else if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in wordsEl.EnumerateArray())
                {
                    var segmentText = word.TryGetProperty("word", out var wt) && wt.ValueKind == JsonValueKind.String
                        ? wt.GetString() ?? string.Empty
                        : string.Empty;

                    var start = word.TryGetProperty("start", out var ws) && ws.ValueKind == JsonValueKind.Number
                        ? (float)ws.GetDouble()
                        : 0f;

                    var end = word.TryGetProperty("end", out var we) && we.ValueKind == JsonValueKind.Number
                        ? (float)we.GetDouble()
                        : start;

                    if (!string.IsNullOrWhiteSpace(segmentText))
                    {
                        segments.Add(new TranscriptionSegment
                        {
                            Text = segmentText,
                            StartSecond = start,
                            EndSecond = end
                        });
                    }
                }
            }

            return new TranscriptionResponse
            {
                Text = text,
                Language = language,
                DurationInSeconds = duration,
                Segments = segments,
                Warnings = warnings,
                Response = new()
                {
                    Timestamp = now,
                    ModelId = model,
                    Body = doc.RootElement.Clone()
                }
            };
        }
        catch (JsonException)
        {
            var isSubtitle = string.Equals(contentType, "text/vtt", StringComparison.OrdinalIgnoreCase)
                             || responseBody.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)
                             || responseBody.Contains("-->", StringComparison.Ordinal);

            var subtitleWarning = isSubtitle
                ? new[] { new { type = "subtitle_format", contentType } }
                : Array.Empty<object>();

            return new TranscriptionResponse
            {
                Text = responseBody,
                Language = null,
                DurationInSeconds = null,
                Segments = [],
                Warnings = warnings.Concat(subtitleWarning),
                Response = new()
                {
                    Timestamp = now,
                    ModelId = model,
                    Body = responseBody
                }
            };
        }
    }
}

