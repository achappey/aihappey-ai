using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.AssemblyAI;
using AIHappey.Core.MCP.Media;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider
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

        // Model: strip provider prefix if present.
        var model = request.Model;

        if (string.IsNullOrWhiteSpace(model))
            model = "best";

        var metadata = request.GetTranscriptionProviderMetadata<AssemblyAITranscriptionProviderMetadata>(GetIdentifier());

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

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

        var bytes = Convert.FromBase64String(audioString);

        // 1) Upload bytes to AssemblyAI: POST /v2/upload (octet-stream) => upload_url
        var uploadUrl = await UploadAsync(bytes, cancellationToken);

        string? transcriptId = null;
        try
        {
            // 2) Create transcript: POST /v2/transcript
            transcriptId = await CreateTranscriptAsync(uploadUrl, model, metadata, cancellationToken);

            // 3) Poll status: GET /v2/transcript/{id}
            var completedJson = await PollTranscriptUntilDoneAsync(transcriptId, cancellationToken);

            // 4) Convert
            return ConvertTranscriptResponse(completedJson, model, now, warnings);
        }
        finally
        {
            // Requirement: delete transcription when done.
            // AssemblyAI guarantees uploaded files are deleted alongside the transcript.
            if (!string.IsNullOrWhiteSpace(transcriptId))
            {
                try
                {
                    await DeleteTranscriptAsync(transcriptId, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Do not fail the whole request solely due to cleanup.
                    warnings.Add(new { type = "cleanup_failed", provider = GetIdentifier(), transcript_id = transcriptId, error = ex.Message });
                }
            }
        }
    }

    private async Task<string> UploadAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "v2/upload")
        {
            Content = new ByteArrayContent(bytes)
        };

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await _client.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AssemblyAI upload failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var uploadUrl = root.TryGetProperty("upload_url", out var u) && u.ValueKind == JsonValueKind.String
            ? (u.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(uploadUrl))
            throw new InvalidOperationException($"AssemblyAI upload response did not contain upload_url. Body: {json}");

        return uploadUrl;
    }

    private async Task<string> CreateTranscriptAsync(
        string uploadUrl,
        string model,
        AssemblyAITranscriptionProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            // required
            ["audio_url"] = uploadUrl,

            // Non-deprecated model selection: use speech_models list.
            ["speech_models"] = new[] { model }
        };

        void Add(string key, object? value)
        {
            if (value is null) return;
            payload[key] = value;
        }

        // Map non-deprecated fields only.
        Add("audio_start_from", metadata?.AudioStartFrom);
        Add("audio_end_at", metadata?.AudioEndAt);

        Add("language_code", metadata?.LanguageCode);
        Add("language_detection", metadata?.LanguageDetection);
        Add("language_confidence_threshold", metadata?.LanguageConfidenceThreshold);

        Add("punctuate", metadata?.Punctuate);
        Add("format_text", metadata?.FormatText);
        Add("disfluencies", metadata?.Disfluencies);

        Add("multichannel", metadata?.Multichannel);

        Add("speaker_labels", metadata?.SpeakerLabels);
        Add("speakers_expected", metadata?.SpeakersExpected);

        Add("auto_chapters", metadata?.AutoChapters);
        Add("auto_highlights", metadata?.AutoHighlights);

        Add("entity_detection", metadata?.EntityDetection);
        Add("sentiment_analysis", metadata?.SentimentAnalysis);
        Add("iab_categories", metadata?.IabCategories);

        Add("filter_profanity", metadata?.FilterProfanity);
        Add("content_safety", metadata?.ContentSafety);
        Add("content_safety_confidence", metadata?.ContentSafetyConfidence);

        Add("redact_pii", metadata?.RedactPii);
        Add("redact_pii_audio", metadata?.RedactPiiAudio);
        Add("redact_pii_audio_quality", metadata?.RedactPiiAudioQuality);
        Add("redact_pii_policies", metadata?.RedactPiiPolicies?.ToArray());
        Add("redact_pii_sub", metadata?.RedactPiiSub);

        Add("summarization", metadata?.Summarization);
        Add("summary_model", metadata?.SummaryModel);
        Add("summary_type", metadata?.SummaryType);

        Add("speech_threshold", metadata?.SpeechThreshold);
        Add("keyterms_prompt", metadata?.KeytermsPrompt?.ToArray());

        if (metadata?.CustomSpelling?.Any() == true)
        {
            Add("custom_spelling", metadata.CustomSpelling.Select(a => new { from = a.From, to = a.To }).ToArray());
        }

        using var content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

        using var resp = await _client.PostAsync("v2/transcript", content, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AssemblyAI create transcript failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? (idEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"AssemblyAI create transcript response did not contain id. Body: {json}");

        return id;
    }

    private async Task<string> PollTranscriptUntilDoneAsync(string transcriptId, CancellationToken cancellationToken)
    {
        // Poll with a bounded backoff.
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var maxWait = TimeSpan.FromMinutes(10);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync($"v2/transcript/{Uri.EscapeDataString(transcriptId)}", cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AssemblyAI get transcript failed ({(int)resp.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? (s.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return json;

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                var err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
                throw new InvalidOperationException($"AssemblyAI transcript failed: {err ?? "unknown error"}. Body: {json}");
            }

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"AssemblyAI transcript did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            delay = delay < maxDelay ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5)) : maxDelay;
        }
    }

    private async Task DeleteTranscriptAsync(string transcriptId, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"v2/transcript/{Uri.EscapeDataString(transcriptId)}");
        using var resp = await _client.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AssemblyAI delete transcript failed ({(int)resp.StatusCode}): {json}");
    }

    private static TranscriptionResponse ConvertTranscriptResponse(
        string json,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? (t.GetString() ?? string.Empty)
            : string.Empty;

        var language = root.TryGetProperty("language_code", out var lang) && lang.ValueKind == JsonValueKind.String
            ? lang.GetString()
            : null;

        var durationInSeconds = root.TryGetProperty("audio_duration", out var dur) && dur.ValueKind == JsonValueKind.Number
            ? (float?)dur.GetDouble()
            : null;

        var segments = new List<TranscriptionSegment>();

        // Prefer utterances (speaker turns) if present.
        if (root.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in utterances.EnumerateArray())
            {
                var uText = u.TryGetProperty("text", out var ut) && ut.ValueKind == JsonValueKind.String
                    ? (ut.GetString() ?? string.Empty)
                    : string.Empty;

                var startMs = u.TryGetProperty("start", out var us) && us.ValueKind == JsonValueKind.Number
                    ? us.GetDouble()
                    : 0d;

                var endMs = u.TryGetProperty("end", out var ue) && ue.ValueKind == JsonValueKind.Number
                    ? ue.GetDouble()
                    : startMs;

                var speaker = u.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String
                    ? sp.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(speaker))
                    uText = $"{speaker}: {uText}";

                if (!string.IsNullOrWhiteSpace(uText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = uText,
                        StartSecond = (float)(startMs / 1000d),
                        EndSecond = (float)(endMs / 1000d)
                    });
                }
            }
        }
        else if (root.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
        {
            // Fallback: per-word segments.
            foreach (var w in words.EnumerateArray())
            {
                var wText = w.TryGetProperty("text", out var wt) && wt.ValueKind == JsonValueKind.String
                    ? (wt.GetString() ?? string.Empty)
                    : string.Empty;

                var startMs = w.TryGetProperty("start", out var ws) && ws.ValueKind == JsonValueKind.Number
                    ? ws.GetDouble()
                    : 0d;

                var endMs = w.TryGetProperty("end", out var we) && we.ValueKind == JsonValueKind.Number
                    ? we.GetDouble()
                    : startMs;

                if (!string.IsNullOrWhiteSpace(wText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = wText,
                        StartSecond = (float)(startMs / 1000d),
                        EndSecond = (float)(endMs / 1000d)
                    });
                }
            }
        }

        return new TranscriptionResponse
        {
            ProviderMetadata = null,
            Text = text,
            Language = language,
            DurationInSeconds = durationInSeconds,
            Segments = segments,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = model,
                Body = json
            }
        };
    }
}

