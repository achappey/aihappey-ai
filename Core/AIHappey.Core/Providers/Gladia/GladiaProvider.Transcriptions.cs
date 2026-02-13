using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Common.Model.Providers.Gladia;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Gladia;

public partial class GladiaProvider 
{
    private const string UploadEndpoint = "v2/upload";
    private const string PreRecordedEndpoint = "v2/pre-recorded";

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

        var audioUrl = await UploadAudioAsync(bytes, request.MediaType, cancellationToken);
        var metadata = request.GetProviderMetadata<GladiaTranscriptionProviderMetadata>(GetIdentifier());
        var jobId = await CreatePreRecordedJobAsync(audioUrl, metadata, cancellationToken);
        var resultJson = await PollUntilDoneAsync(jobId, cancellationToken);


        var result = ConvertTranscriptionResponse(resultJson, request.Model, now);

        await DeletePreRecordedJobAsync(jobId, cancellationToken);

        return result;

    }

    private async Task<string> UploadAudioAsync(
        byte[] bytes,
        string mediaType,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + mediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        form.Add(file, "audio", fileName);

        using var resp = await _client.PostAsync(UploadEndpoint, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gladia upload failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var audioUrl = root.TryGetProperty("audio_url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
            ? (urlEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"Gladia upload response did not contain audio_url. Body: {json}");

        return audioUrl;
    }

    private async Task<string> CreatePreRecordedJobAsync(
        string audioUrl,
        GladiaTranscriptionProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["audio_url"] = audioUrl
        };

        if (metadata is not null)
        {
            AddIfHasValue(payload, "custom_vocabulary", metadata.CustomVocabulary);
            AddIfHasValue(payload, "custom_vocabulary_config", metadata.CustomVocabularyConfig);
            AddIfHasValue(payload, "subtitles", metadata.Subtitles);
            AddIfHasValue(payload, "subtitles_config", metadata.SubtitlesConfig);
            AddIfHasValue(payload, "diarization", metadata.Diarization);
            AddIfHasValue(payload, "diarization_config", metadata.DiarizationConfig);
            AddIfHasValue(payload, "translation", metadata.Translation);
            AddIfHasValue(payload, "translation_config", metadata.TranslationConfig);
            AddIfHasValue(payload, "summarization", metadata.Summarization);
            AddIfHasValue(payload, "summarization_config", metadata.SummarizationConfig);
            AddIfHasValue(payload, "moderation", metadata.Moderation);
            AddIfHasValue(payload, "named_entity_recognition", metadata.NamedEntityRecognition);
            AddIfHasValue(payload, "chapterization", metadata.Chapterization);
            AddIfHasValue(payload, "name_consistency", metadata.NameConsistency);
            AddIfHasValue(payload, "custom_spelling", metadata.CustomSpelling);
            AddIfHasValue(payload, "custom_spelling_config", metadata.CustomSpellingConfig);
            AddIfHasValue(payload, "structured_data_extraction", metadata.StructuredDataExtraction);
            AddIfHasValue(payload, "structured_data_extraction_config", metadata.StructuredDataExtractionConfig);
            AddIfHasValue(payload, "sentiment_analysis", metadata.SentimentAnalysis);
            AddIfHasValue(payload, "audio_to_llm", metadata.AudioToLlm);
            AddIfHasValue(payload, "audio_to_llm_config", metadata.AudioToLlmConfig);
            AddIfHasValue(payload, "custom_metadata", metadata.CustomMetadata);
            AddIfHasValue(payload, "sentences", metadata.Sentences);
            AddIfHasValue(payload, "display_mode", metadata.DisplayMode);
            AddIfHasValue(payload, "punctuation_enhanced", metadata.PunctuationEnhanced);
            AddIfHasValue(payload, "language_config", metadata.LanguageConfig);
        }

        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _client.PostAsync(PreRecordedEndpoint, body, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gladia pre-recorded init failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? (idEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Gladia pre-recorded init response did not contain id. Body: {json}");

        return id;
    }

    private static void AddIfHasValue(IDictionary<string, object?> payload, string key, bool? value)
    {
        if (value is not null)
            payload[key] = value;
    }

    private static void AddIfHasValue(IDictionary<string, object?> payload, string key, JsonElement? value)
    {
        if (value is null)
            return;

        var element = value.Value;
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        payload[key] = element;
    }

    private async Task<string> PollUntilDoneAsync(string jobId, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var maxWait = TimeSpan.FromMinutes(5);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync($"{PreRecordedEndpoint}/{Uri.EscapeDataString(jobId)}", cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Gladia pre-recorded status failed ({(int)resp.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? (statusEl.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                return json;

            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Gladia pre-recorded job failed. Body: {json}");

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"Gladia pre-recorded job did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            delay = delay < maxDelay
                ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5))
                : maxDelay;
        }
    }

    private async Task DeletePreRecordedJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var resp = await _client.DeleteAsync($"{PreRecordedEndpoint}/{Uri.EscapeDataString(jobId)}", cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception(body);
        }
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string modelId, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object
            ? resultEl
            : default;

        var transcription = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("transcription", out var transcriptionEl)
            ? transcriptionEl
            : default;

        var text = transcription.ValueKind == JsonValueKind.Object && transcription.TryGetProperty("full_transcript", out var textEl)
            ? (textEl.GetString() ?? string.Empty)
            : string.Empty;

        var segments = new List<TranscriptionSegment>();
        if (transcription.ValueKind == JsonValueKind.Object &&
            transcription.TryGetProperty("utterances", out var utterancesEl) &&
            utterancesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in utterancesEl.EnumerateArray())
            {
                var uText = u.TryGetProperty("text", out var tEl) ? (tEl.GetString() ?? string.Empty) : string.Empty;
                var start = u.TryGetProperty("start", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? (float)sEl.GetDouble() : 0f;
                var end = u.TryGetProperty("end", out var eEl) && eEl.ValueKind == JsonValueKind.Number ? (float)eEl.GetDouble() : start;

                if (u.TryGetProperty("speaker", out var spEl) && spEl.ValueKind == JsonValueKind.Number)
                {
                    var speaker = spEl.GetInt32();
                    uText = $"Speaker {speaker}: {uText}";
                }

                if (!string.IsNullOrWhiteSpace(uText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = uText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = modelId,
                Body = json
            }
        };
    }
}
