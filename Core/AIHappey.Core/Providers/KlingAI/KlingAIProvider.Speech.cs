using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KlingAI;

public partial class KlingAIProvider
{
    private static readonly JsonSerializerOptions SpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private async Task<SpeechResponse> SpeechRequestInternal(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException();

        float? duration = null;
        string? callbackUrl = null;
        string? externalTaskId = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("duration", out var durationElement))
                duration = durationElement.Deserialize<float?>(JsonSerializerOptions.Web);

            if (metadata.TryGetProperty("callback_url", out var callbackUrlElement))
                callbackUrl = callbackUrlElement.Deserialize<string>(JsonSerializerOptions.Web);

            if (metadata.TryGetProperty("external_task_id", out var externalTaskIdElement))
                externalTaskId = externalTaskIdElement.Deserialize<string>(JsonSerializerOptions.Web);
        }

        if (duration is null)
            throw new ArgumentException("KlingAI requires duration (seconds). Provide providerOptions.klingai.duration.", nameof(request));

        if (duration is < 3.0f or > 10.0f)
            throw new ArgumentOutOfRangeException("Duration", "KlingAI duration must be between 3.0 and 10.0 seconds.");

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var outputFormat = request.OutputFormat?.Trim();
        if (string.IsNullOrWhiteSpace(outputFormat))
            outputFormat = "mp3";

        var normalizedFormat = outputFormat.ToLowerInvariant();
        if (normalizedFormat is not ("mp3" or "wav"))
        {
            warnings.Add(new { type = "unsupported", feature = "outputFormat", details = "Only mp3 or wav supported; defaulted to mp3." });
            normalizedFormat = "mp3";
        }

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Text,
            ["duration"] = duration,
            ["callback_url"] = callbackUrl,
            ["external_task_id"] = externalTaskId
        };

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["prompt"] = request.Text;
        payload["duration"] = duration;
        payload["callback_url"] = callbackUrl;
        payload["external_task_id"] = externalTaskId;

        // Null-ignore options do not omit dictionary entries; omit only known optional fields.
        if (callbackUrl is null)
            payload.Remove("callback_url");

        if (externalTaskId is null)
            payload.Remove("external_task_id");

        var json = JsonSerializer.Serialize(payload, SpeechJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/audio/text-to-audio")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"KlingAI speech create failed ({(int)createResp.StatusCode})"
                : $"KlingAI speech create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement;

        EnsureKlingOk(createRoot, "speech_create");
        var taskId = ExtractSpeechTaskId(createRoot);
        var final = await PollSpeechTaskAsync(taskId, cancellationToken);
        var (audioBytes, mime) = await ExtractSpeechAudioAsync(final, normalizedFormat, cancellationToken);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = normalizedFormat
            },
            Warnings = warnings,
            Request = new()
            {
                Body = payload
            },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string ExtractSpeechTaskId(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("task_id", out var taskIdEl) && taskIdEl.ValueKind == JsonValueKind.String)
            {
                var taskId = taskIdEl.GetString();
                if (!string.IsNullOrWhiteSpace(taskId))
                    return taskId;
            }
        }

        throw new InvalidOperationException("No task_id returned from KlingAI audio API.");
    }

    private async Task<JsonElement> PollSpeechTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: async ct =>
            {
                using var pollResp = await _client.GetAsync($"v1/audio/text-to-audio/{taskId}", ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"KlingAI speech poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            isTerminal: r =>
            {
                var status = GetSpeechTaskStatus(r);
                return status is "succeed" or "failed";
            },
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = GetSpeechTaskStatus(final);
        if (status == "failed")
        {
            var msg = TryGetSpeechStatusMessage(final) ?? "KlingAI speech task failed.";
            throw new InvalidOperationException(msg);
        }

        return final;
    }

    private static string? GetSpeechTaskStatus(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
            return statusEl.GetString();

        return null;
    }

    private static string? TryGetSpeechStatusMessage(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;

        if (data.TryGetProperty("task_status_msg", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
            return msgEl.GetString();

        return null;
    }

    private async Task<(byte[] Bytes, string Mime)> ExtractSpeechAudioAsync(JsonElement root, string outputFormat, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("KlingAI speech poll response missing data object.");

        if (!data.TryGetProperty("task_result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("KlingAI speech poll response missing task_result.");

        if (!result.TryGetProperty("audios", out var audiosEl) || audiosEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("KlingAI speech poll response missing audios array.");

        foreach (var audio in audiosEl.EnumerateArray())
        {
            var url = ResolveSpeechUrl(audio, outputFormat);
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var audioResp = await _client.GetAsync(url, cancellationToken);
            var bytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!audioResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Failed to download KlingAI audio: {audioResp.StatusCode}");

            var mime = ResolveSpeechMime(outputFormat, audioResp.Content.Headers.ContentType?.MediaType);
            return (bytes, mime);
        }

        throw new InvalidOperationException("KlingAI returned no audio.");
    }

    private static string ResolveSpeechMime(string? outputFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        if (string.Equals(outputFormat, "wav", StringComparison.OrdinalIgnoreCase))
            return "audio/wav";

        return "audio/mpeg";
    }

    private static string? ResolveSpeechUrl(JsonElement audio, string outputFormat)
    {
        if (string.Equals(outputFormat, "wav", StringComparison.OrdinalIgnoreCase))
        {
            if (audio.TryGetProperty("url_wav", out var wavEl) && wavEl.ValueKind == JsonValueKind.String)
                return wavEl.GetString();
        }

        if (audio.TryGetProperty("url_mp3", out var mp3El) && mp3El.ValueKind == JsonValueKind.String)
            return mp3El.GetString();

        return null;
    }
}
