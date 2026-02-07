using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Model.Providers.Speechmatics;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Speechmatics;

public partial class SpeechmaticsProvider
{
    private const string BatchBaseUrl = "https://asr.api.speechmatics.com";
    private const int MaxPollAttempts = 180;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var metadata = request.GetProviderMetadata<SpeechmaticsTranscriptionProviderMetadata>(GetIdentifier());

        var jobId = await CreateJobAsync(request, metadata, bytes, cancellationToken);
        var finalJobJson = await PollUntilTerminalAsync(jobId, cancellationToken);
        var transcriptJson = await GetTranscriptAsync(jobId, cancellationToken);

        return ConvertSpeechmaticsResponse(transcriptJson, finalJobJson, request.Model);
    }

    private async Task<string> CreateJobAsync(
        TranscriptionRequest request,
        SpeechmaticsTranscriptionProviderMetadata? metadata,
        byte[] audioBytes,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        form.Add(new ByteArrayContent(audioBytes), "data_file", fileName);

        var config = BuildConfigJson(request.Model, metadata);
        form.Add(new StringContent(config), "config");

        using var resp = await _client.PostAsync($"{BatchBaseUrl}/v2/jobs", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics transcription job creation failed ({(int)resp.StatusCode}): {json}");

        var jobId = TryReadJobId(json);
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException($"Speechmatics transcription job creation response did not include a job id: {json}");

        return jobId;
    }

    private static string BuildConfigJson(string model, SpeechmaticsTranscriptionProviderMetadata? metadata)
    {
        var transcriptionConfig = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            transcriptionConfig["language"] = metadata.Language;

        transcriptionConfig["operating_point"] = model;

        var config = new Dictionary<string, object?>
        {
            ["type"] = "transcription",
            ["transcription_config"] = transcriptionConfig
        };

        return JsonSerializer.Serialize(config);
    }

    private async Task<string> PollUntilTerminalAsync(string jobId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            using var resp = await _client.GetAsync($"{BatchBaseUrl}/v2/jobs/{jobId}", cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Speechmatics transcription job polling failed ({(int)resp.StatusCode}): {json}");

            var status = TryReadJobStatus(json);
            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                return json;

            if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Speechmatics transcription job was rejected: {json}");

            await Task.Delay(PollInterval, cancellationToken);
        }

        throw new TimeoutException($"Speechmatics transcription job polling exceeded {MaxPollAttempts} attempts.");
    }

    private async Task<string> GetTranscriptAsync(string jobId, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync($"{BatchBaseUrl}/v2/jobs/{jobId}/transcript?format=json-v2", cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Speechmatics transcript fetch failed ({(int)resp.StatusCode}): {json}");

        return json;
    }

    private static TranscriptionResponse ConvertSpeechmaticsResponse(string transcriptJson, string finalJobJson, string model)
    {
        using var transcriptDoc = JsonDocument.Parse(transcriptJson);
        using var jobDoc = JsonDocument.Parse(finalJobJson);

        var transcriptRoot = transcriptDoc.RootElement;
        var jobRoot = jobDoc.RootElement;

        var segments = new List<TranscriptionSegment>();
        var textBuilder = new System.Text.StringBuilder();

        if (transcriptRoot.TryGetProperty("results", out var resultsEl) &&
            resultsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var result in resultsEl.EnumerateArray())
            {
                var type = result.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (!result.TryGetProperty("alternatives", out var alternativesEl) ||
                    alternativesEl.ValueKind != JsonValueKind.Array)
                    continue;

                var firstAlternative = alternativesEl.EnumerateArray().FirstOrDefault();
                if (firstAlternative.ValueKind == JsonValueKind.Undefined)
                    continue;

                var content = firstAlternative.TryGetProperty("content", out var contentEl)
                    ? contentEl.GetString() ?? string.Empty
                    : string.Empty;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    var attachesToPrevious = result.TryGetProperty("attaches_to", out var attachEl)
                        && string.Equals(attachEl.GetString(), "previous", StringComparison.OrdinalIgnoreCase);

                    if (textBuilder.Length > 0 && !attachesToPrevious)
                        textBuilder.Append(' ');

                    textBuilder.Append(content);
                }

                if ((string.Equals(type, "word", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type, "punctuation", StringComparison.OrdinalIgnoreCase)) &&
                    result.TryGetProperty("start_time", out var startEl) &&
                    result.TryGetProperty("end_time", out var endEl) &&
                    startEl.ValueKind == JsonValueKind.Number &&
                    endEl.ValueKind == JsonValueKind.Number)
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = content,
                        StartSecond = (float)startEl.GetDouble(),
                        EndSecond = (float)endEl.GetDouble()
                    });
                }
            }
        }

        var language = TryReadString(transcriptRoot, "metadata", "transcription_config", "language");
        var duration = TryReadFloat(jobRoot, "job", "duration");

        return new TranscriptionResponse
        {
            Text = textBuilder.ToString(),
            Segments = segments,
            Language = language,
            DurationInSeconds = duration,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = transcriptJson
            }
        };
    }

    private static string? TryReadJobId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            return idEl.GetString();

        if (root.TryGetProperty("job", out var jobEl) &&
            jobEl.ValueKind == JsonValueKind.Object &&
            jobEl.TryGetProperty("id", out var nestedIdEl) &&
            nestedIdEl.ValueKind == JsonValueKind.String)
        {
            return nestedIdEl.GetString();
        }

        return null;
    }

    private static string? TryReadJobStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            return statusEl.GetString();

        if (root.TryGetProperty("job", out var jobEl) &&
            jobEl.ValueKind == JsonValueKind.Object &&
            jobEl.TryGetProperty("status", out var nestedStatusEl) &&
            nestedStatusEl.ValueKind == JsonValueKind.String)
        {
            return nestedStatusEl.GetString();
        }

        return null;
    }

    private static string? TryReadString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static float? TryReadFloat(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.Number ? (float)current.GetDouble() : null;
    }

}

