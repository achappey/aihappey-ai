using AIHappey.Core.AI;
using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Novita;
using System.Text.Json;
using System.Text;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    public async Task<SpeechResponse> SpeechRequestAsyncTxt2Speech(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata =
            request.GetSpeechProviderMetadata<NovitaSpeechProviderMetadata>(GetIdentifier());

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        // ---- required by Novita ----
        var voiceId =
            request.Voice
            ?? metadata?.Text2Speech?.VoiceId
            ?? "Emily"; // Novita docs examples + voice list :contentReference[oaicite:1]{index=1}

        var language =
            metadata?.Text2Speech?.Language
            ?? "en-US"; // Novita requires language; default to en-US :contentReference[oaicite:2]{index=2}

        // ---- output audio type ----
        var audioType =
            request.OutputFormat
            //  ?? metadata?.Text2Speech?.
            ?? "wav";

        audioType = audioType.Equals("mp3", StringComparison.OrdinalIgnoreCase) ? "mp3" : "wav";

        // ---- speed/volume (optional) ----
        // Novita accepts volume [1.0..2.0], speed [0.8..3.0] :contentReference[oaicite:3]{index=3}
        double? speed = request.Speed ?? metadata?.Text2Speech?.Speed;
        double? volume = metadata?.Text2Speech?.Volume;

        var submitPayload = new Dictionary<string, object?>
        {
            ["extra"] = new Dictionary<string, object?>
            {
                ["response_audio_type"] = audioType,
            },
            ["request"] = new Dictionary<string, object?>
            {
                ["voice_id"] = voiceId,
                ["language"] = language,
                ["texts"] = new[] { request.Text ?? "" },
            }
        };

        if (speed is not null)
            ((Dictionary<string, object?>)submitPayload["request"]!)["speed"] = speed;

        if (volume is not null)
            ((Dictionary<string, object?>)submitPayload["request"]!)["volume"] = volume;

        using var submitContent = new StringContent(
            JsonSerializer.Serialize(submitPayload),
            Encoding.UTF8,
            "application/json"
        );

        using var submitResp = await _client.PostAsync(
            BaseUrl + "async/" + request.Model,
            submitContent,
            cancellationToken
        );

        var submitJson = await submitResp.Content.ReadAsStringAsync(cancellationToken);

        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Novita TTS submit failed ({(int)submitResp.StatusCode}): {submitJson}"
            );

        var taskId = ReadTaskId(submitJson);

        // ---- poll task result until finished ----
        var taskResultJson = await PollTaskResultJson(taskId, cancellationToken);

        // ---- extract audio url ----
        var (status, reason, audioUrl) = ReadAudioUrl(taskResultJson);

        if (!string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Novita TTS task not successful (status={status}): {reason}\n{taskResultJson}"
            );

        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException(
                $"Novita TTS returned SUCCEED but no audio_url was found.\n{taskResultJson}"
            );

        // audio_url is typically a direct downloadable URL :contentReference[oaicite:4]{index=4}
        var bytes = await DownloadPresignedAsync(audioUrl, cancellationToken);

        var mime = audioType == "mp3" ? "audio/mpeg" : "audio/wav";

        var base64 = Convert
            .ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = base64,
                MimeType = mime,
                Format = audioType ?? "wav"
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = taskResultJson
            }
        };
    }

    private static string ReadTaskId(string submitJson)
    {
        using var doc = JsonDocument.Parse(submitJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("task_id", out var tid) && tid.ValueKind == JsonValueKind.String)
            return tid.GetString() ?? throw new InvalidOperationException("Novita: task_id was null.");

        // fallback if they ever wrap it
        if (root.TryGetProperty("task", out var task) &&
            task.ValueKind == JsonValueKind.Object &&
            task.TryGetProperty("task_id", out var tid2) &&
            tid2.ValueKind == JsonValueKind.String)
            return tid2.GetString() ?? throw new InvalidOperationException("Novita: task.task_id was null.");

        throw new InvalidOperationException($"Novita: could not find task_id in response: {submitJson}");
    }

    private static async Task<byte[]> DownloadPresignedAsync(string url, CancellationToken ct)
    {
        // IMPORTANT: presigned URLs should be called without extra auth headers :contentReference[oaicite:2]{index=2}
        using var http = new HttpClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // S3 usually returns XML in the body; this makes the real reason visible
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException(
                $"Presigned download failed ({(int)resp.StatusCode}): {body}"
            );
        }

        return bytes;
    }


    private async Task<string> PollTaskResultJson(string taskId, CancellationToken ct)
    {
        // “Best effort” polling: quick early retries, then backoff.
        const int maxAttempts = 30;
        var delayMs = 350;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var resp = await _client.GetAsync(
                TaskResultUrl + Uri.EscapeDataString(taskId),
                ct
            );

            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // retry on transient stuff
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    await Task.Delay(delayMs, ct);
                    delayMs = Math.Min((int)(delayMs * 1.6), 4000);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Novita task-result failed ({(int)resp.StatusCode}): {json}"
                );
            }

            var status = ReadTaskStatus(json);

            if (string.Equals(status, "TASK_STATUS_SUCCEED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "TASK_STATUS_FAILED", StringComparison.OrdinalIgnoreCase))
                return json;

            // queued/processing → wait + try again
            await Task.Delay(delayMs, ct);
            delayMs = Math.Min((int)(delayMs * 1.4), 3000);
        }

        throw new TimeoutException($"Novita task {taskId} did not finish within polling limits.");
    }

    private static string ReadTaskStatus(string taskResultJson)
    {
        using var doc = JsonDocument.Parse(taskResultJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("task", out var task) &&
            task.ValueKind == JsonValueKind.Object &&
            task.TryGetProperty("status", out var s) &&
            s.ValueKind == JsonValueKind.String)
            return s.GetString() ?? "";

        return "";
    }

    private static (string Status, string Reason, string? AudioUrl) ReadAudioUrl(string taskResultJson)
    {
        using var doc = JsonDocument.Parse(taskResultJson);
        var root = doc.RootElement;

        var status = "";
        var reason = "";

        if (root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
        {
            if (task.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                status = s.GetString() ?? "";

            if (task.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                reason = r.GetString() ?? "";
        }

        string? audioUrl = null;

        if (root.TryGetProperty("audios", out var audios) &&
            audios.ValueKind == JsonValueKind.Array &&
            audios.GetArrayLength() > 0)
        {
            var first = audios[0];
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("audio_url", out var u) &&
                u.ValueKind == JsonValueKind.String)
            {
                audioUrl = u.GetString();
            }
        }

        return (status, reason, audioUrl);
    }

}
