using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SunoAPI;

public partial class SunoAPIProvider
{
    private sealed record SunoSpeechTaskResult(string Status, string TaskId, JsonElement RawRoot);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var prompt = request.Text.Trim();
        if (prompt.Length > 500)
            throw new ArgumentOutOfRangeException(nameof(request), "Suno non-custom mode prompt must be 500 characters or fewer.");

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? "V4_5ALL"
            : request.Model.Trim().ToUpperInvariant();

        var now = DateTime.UtcNow;
        var http = _httpContextAccessor.HttpContext;

        var baseUrl =
            $"{http?.Request.Scheme}://{http?.Request.Host}";

        var startPayload = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["customMode"] = false,
            ["instrumental"] = false,
            ["model"] = model,
            // Endpoint requires a callback URL; polling is still used for completion.
            ["callBackUrl"] = $"{baseUrl}/api/callbacks/suno"
        };

        using var startRequest = new HttpRequestMessage(HttpMethod.Post, "api/v1/generate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(startPayload),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var startResponse = await _client.SendAsync(startRequest, cancellationToken);
        var startRaw = await startResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!startResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Suno generate request failed ({(int)startResponse.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var startRoot = startDoc.RootElement;

        var taskId = startRoot.TryGetProperty("data", out var startData)
            && startData.TryGetProperty("taskId", out var taskIdEl)
            ? taskIdEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Suno response missing data.taskId.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollSpeechTaskAsync(taskId, ct),
            isTerminal: r => r.Status is "SUCCESS"
                or "CREATE_TASK_FAILED"
                or "GENERATE_AUDIO_FAILED"
                or "CALLBACK_EXCEPTION"
                or "SENSITIVE_WORD_ERROR",
            interval: TimeSpan.FromSeconds(2),
            timeout: null,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.Status != "SUCCESS")
            throw new InvalidOperationException($"Suno task failed (taskId={taskId}, status={completed.Status}).");

        var audioUrl = TryGetFirstAudioUrl(completed.RawRoot);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"Suno task succeeded but response.data.response.sunoData[0].audioUrl is missing (taskId={taskId}).");

        using var fileResponse = await _client.GetAsync(audioUrl, cancellationToken);
        var fileBytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResponse.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Suno audio download failed ({(int)fileResponse.StatusCode}): {err}");
        }

        var mimeType = fileResponse.Content.Headers.ContentType?.MediaType
            ?? GuessAudioMimeType(audioUrl)
            ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(fileBytes),
                MimeType = mimeType,
                Format = MapAudioFormat(mimeType)
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    taskId = completed.TaskId,
                    status = completed.Status,
                    raw = completed.RawRoot.Clone()
                })
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = completed.RawRoot.Clone()
            }
        };
    }

    private async Task<SunoSpeechTaskResult> PollSpeechTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"api/v1/generate/record-info?taskId={Uri.EscapeDataString(taskId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Suno poll request failed ({(int)pollResponse.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();

        var status = root.TryGetProperty("data", out var data)
            && data.TryGetProperty("status", out var statusEl)
            ? statusEl.GetString() ?? "UNKNOWN"
            : "UNKNOWN";

        var returnedTaskId = data.TryGetProperty("taskId", out var returnedTaskIdEl)
            ? returnedTaskIdEl.GetString() ?? taskId
            : taskId;

        return new SunoSpeechTaskResult(status.ToUpperInvariant(), returnedTaskId, root);
    }

    private static string? TryGetFirstAudioUrl(JsonElement taskRoot)
    {
        if (!taskRoot.TryGetProperty("data", out var data)
            || !data.TryGetProperty("response", out var response)
            || !response.TryGetProperty("sunoData", out var sunoData)
            || sunoData.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in sunoData.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("audioUrl", out var audioUrlEl))
            {
                var value = audioUrlEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private static string MapAudioFormat(string mimeType)
    {
        var mt = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return mt switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" => "m4a",
            "audio/webm" => "webm",
            _ => "mp3"
        };
    }

    private static string? GuessAudioMimeType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp3")) return "audio/mpeg";
        if (u.Contains(".wav")) return "audio/wav";
        if (u.Contains(".ogg")) return "audio/ogg";
        if (u.Contains(".opus")) return "audio/opus";
        if (u.Contains(".flac")) return "audio/flac";
        if (u.Contains(".aac")) return "audio/aac";
        if (u.Contains(".m4a") || u.Contains(".mp4")) return "audio/mp4";
        if (u.Contains(".webm")) return "audio/webm";
        return null;
    }
}
