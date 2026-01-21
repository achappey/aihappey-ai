using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string SoundEffectsPath = "/v1/ai/sound-effects";

    private sealed record FreepikSoundEffectsTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<SpeechResponse> SoundEffectsSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (!request.Model.Equals("sound-effects", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Freepik speech model '{request.Model}' is not supported.");
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetSpeechProviderMetadata<FreepikSpeechProviderMetadata>(GetIdentifier());
        var se = metadata?.SoundEffects;

        // docs: duration_seconds required (0.5 - 22). We accept int seconds in metadata.
        var durationSeconds = se?.DurationSeconds ?? 5;
        if (durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(FreepikSpeechProviderMetadata.SoundEffects), "sound_effects.duration_seconds must be > 0.");
        if (durationSeconds > 22)
            throw new ArgumentOutOfRangeException(nameof(FreepikSpeechProviderMetadata.SoundEffects), "sound_effects.duration_seconds must be <= 22.");

        var startPayload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["duration_seconds"] = durationSeconds
            // webhook_url intentionally omitted (per user instruction: polling)
        };

        if (se?.PromptInfluence is { } pi)
        {
            if (pi < 0 || pi > 1)
                throw new ArgumentOutOfRangeException(nameof(FreepikSpeechProviderMetadata.SoundEffects), "sound_effects.prompt_influence must be between 0 and 1.");
            startPayload["prompt_influence"] = pi;
        }

        if (se?.Loop is not null)
            startPayload["loop"] = se.Loop.Value;

        var startJson = JsonSerializer.Serialize(startPayload, JsonOpts);

        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + SoundEffectsPath)
        {
            Content = new StringContent(startJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik sound-effects start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        // Poll GET /v1/ai/sound-effects/{task-id} until COMPLETED/FAILED.
        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => SoundEffectsPollAsync(taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromMilliseconds(800),
            timeout: TimeSpan.FromSeconds(90),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik sound-effects task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik sound-effects task completed but returned no generated URLs (task_id={taskId}).");

        // Download final asset.
        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik sound-effects download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        var base64 = Convert.ToBase64String(fileBytes);

        using var finalDoc = JsonDocument.Parse(final.Raw);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = "audio/mpeg",
                Format = "mp3"
            },
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = finalDoc.RootElement.Clone()
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["freepik"] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    status = final.Status,
                    generated = final.Generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private async Task<FreepikSoundEffectsTaskResult> SoundEffectsPollAsync(string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{SoundEffectsPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik sound-effects poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var data = doc.RootElement.GetProperty("data");
        var status = data.GetProperty("status").GetString() ?? "UNKNOWN";
        var returnedTaskId = data.GetProperty("task_id").GetString() ?? taskId;

        List<string>? generated = null;
        if (data.TryGetProperty("generated", out var genEl) && genEl.ValueKind == JsonValueKind.Array)
        {
            generated = [.. genEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))];
        }

        return new FreepikSoundEffectsTaskResult(status, generated, raw, returnedTaskId);
    }
}

