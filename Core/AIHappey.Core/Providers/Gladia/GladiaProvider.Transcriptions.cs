using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

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
        var metadata = request.GetProviderMetadata<Dictionary<string, JsonElement>>(GetIdentifier());
        var createJob = await CreatePreRecordedJobAsync(audioUrl, metadata, cancellationToken);
        var jobId = createJob.Id;
        var transcriptionSucceeded = false;
        try
        {
            var resultJson = await PollUntilDoneAsync(jobId, cancellationToken);
            var currentModel = await this.GetModel(request.Model, cancellationToken);

            var result = ConvertTranscriptionResponse(
                    resultJson,
                    request.Model.ToModelId(GetIdentifier()),
                    now,
                    createJob.RequestBody,
                    GetIdentifier(),
                    currentModel?.Pricing?.Input);

            transcriptionSucceeded = true;
            return result;
        }
        finally
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await DeletePreRecordedJobAsync(jobId, cleanupCancellation.Token);
            }
            catch when (!transcriptionSucceeded)
            {
            }
        }

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

    private async Task<(string Id, string RequestBody)> CreatePreRecordedJobAsync(
        string audioUrl,
        Dictionary<string, JsonElement>? metadata,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["audio_url"] = audioUrl
        };

        if (metadata is not null)
            ValidateBooleanMetadata(metadata);

        MergeRawMetadataPassthrough(payload, metadata);

        var requestBody = JsonSerializer.Serialize(payload);
        var body = new StringContent(requestBody, Encoding.UTF8, "application/json");

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

        return (id, requestBody);
    }

    private static void ValidateBooleanMetadata(Dictionary<string, JsonElement> metadata)
    {
        foreach (var (name, value) in metadata)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            switch (NormalizeMetadataName(name))
            {
                case "subtitles":
                case "diarization":
                case "translation":
                case "summarization":
                case "moderation":
                case "named_entity_recognition":
                case "chapterization":
                case "name_consistency":
                case "custom_spelling":
                case "structured_data_extraction":
                case "sentiment_analysis":
                case "audio_to_llm":
                case "sentences":
                case "display_mode":
                case "punctuation_enhanced":
                    _ = value.Deserialize<bool>();
                    break;
            }
        }
    }

    private static void MergeRawMetadataPassthrough(
        IDictionary<string, object?> payload,
        Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
            return;

        foreach (var (name, value) in metadata)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[NormalizeMetadataName(name)] = value.Clone();
        }
    }

    private static string NormalizeMetadataName(string name)
        => name.ToLowerInvariant() switch
        {
            "custom_vocabulary" => "custom_vocabulary",
            "custom_vocabulary_config" => "custom_vocabulary_config",
            "subtitles" => "subtitles",
            "subtitles_config" => "subtitles_config",
            "diarization" => "diarization",
            "diarization_config" => "diarization_config",
            "translation" => "translation",
            "translation_config" => "translation_config",
            "summarization" => "summarization",
            "summarization_config" => "summarization_config",
            "moderation" => "moderation",
            "named_entity_recognition" => "named_entity_recognition",
            "chapterization" => "chapterization",
            "name_consistency" => "name_consistency",
            "custom_spelling" => "custom_spelling",
            "custom_spelling_config" => "custom_spelling_config",
            "structured_data_extraction" => "structured_data_extraction",
            "structured_data_extraction_config" => "structured_data_extraction_config",
            "sentiment_analysis" => "sentiment_analysis",
            "audio_to_llm" => "audio_to_llm",
            "audio_to_llm_config" => "audio_to_llm_config",
            "custom_metadata" => "custom_metadata",
            "sentences" => "sentences",
            "display_mode" => "display_mode",
            "punctuation_enhanced" => "punctuation_enhanced",
            "language_config" => "language_config",
            _ => name
        };

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

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string modelId,
        DateTime timestamp,
        string requestBody,
        string providerKey,
        decimal? inputPricePerSecond)
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

                if (u.TryGetProperty("speaker", out var spEl) &&
                    spEl.ValueKind == JsonValueKind.Number &&
                    spEl.TryGetInt32(out var speaker))
                {
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

        var gladiaMetadata = result.ValueKind == JsonValueKind.Object &&
                             result.TryGetProperty("metadata", out var metadataEl)
            ? metadataEl.Clone()
            : default;

        var billingTime = gladiaMetadata.TryGetProperty("billing_time", out var billingTimeEl) &&
                          billingTimeEl.ValueKind == JsonValueKind.Number
            ? billingTimeEl.GetDecimal()
            : (decimal?)null;

        var audioDuration = gladiaMetadata.TryGetProperty("audio_duration", out var audioDurationEl) &&
                            audioDurationEl.ValueKind == JsonValueKind.Number
            ? audioDurationEl.GetDecimal()
            : (decimal?)null;

        var transcriptionTime = gladiaMetadata.TryGetProperty("transcription_time", out var transcriptionTimeEl) &&
                                transcriptionTimeEl.ValueKind == JsonValueKind.Number
            ? transcriptionTimeEl.GetDecimal()
            : (decimal?)null;

        var cost = billingTime is not null && inputPricePerSecond is not null
            ? billingTime.Value * inputPricePerSecond.Value
            : (decimal?)null;

        var gladiaProviderMetadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["metadata"] = gladiaMetadata.ValueKind == JsonValueKind.Undefined
                ? null
                : gladiaMetadata
        });

        var language = default(string);

        if (transcription.ValueKind == JsonValueKind.Object &&
            transcription.TryGetProperty("languages", out var languagesEl) &&
            languagesEl.ValueKind == JsonValueKind.Array)
        {
            language = languagesEl
                .EnumerateArray()
                .FirstOrDefault(x => x.ValueKind == JsonValueKind.String)
                .GetString();
        }

        if (string.IsNullOrWhiteSpace(language) &&
            transcription.ValueKind == JsonValueKind.Object &&
            transcription.TryGetProperty("utterances", out var languageUtterancesEl) &&
            languageUtterancesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in languageUtterancesEl.EnumerateArray())
            {
                if (u.TryGetProperty("language", out var langEl) &&
                    langEl.ValueKind == JsonValueKind.String)
                {
                    language = langEl.GetString();
                    break;
                }
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Segments = segments,
            Language = language,
            DurationInSeconds = audioDuration is null
                ? null
                : (float)audioDuration.Value,
            ProviderMetadata = providerKey.CreatePrimitiveProviderMetadata(
                    gladiaProviderMetadata, cost
            ),
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = modelId,
                Body = json
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }
}
