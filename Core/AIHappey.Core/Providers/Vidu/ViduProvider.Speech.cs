using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Vidu;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{
    private static readonly JsonSerializerOptions ViduSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ViduSpeechCreationResult(string State, JsonElement RawRoot);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var speechMetadata = request.GetProviderMetadata<ViduSpeechProviderMetadata>(GetIdentifier());

        var model = request.Model.Trim();
        var isTextToAudio = string.Equals(model, "audio1.0", StringComparison.OrdinalIgnoreCase);
        var isTextToSpeech = string.Equals(model, "audio-tts", StringComparison.OrdinalIgnoreCase);

        if (!isTextToAudio && !isTextToSpeech)
            throw new NotSupportedException($"Vidu speech model '{request.Model}' is not supported.");

        if (isTextToAudio)
            return await ViduTextToAudioAsync(request, speechMetadata, warnings, now, cancellationToken);

        return await ViduTextToSpeechAsync(request, speechMetadata, warnings, now, cancellationToken);
    }

    private async Task<SpeechResponse> ViduTextToAudioAsync(
        SpeechRequest request,
        ViduSpeechProviderMetadata? metadata,
        List<object> warnings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // Text-to-audio supports prompt + duration/seed. Unified knobs do not map.
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

        if (metadata?.Duration is { } duration && (duration < 2f || duration > 10f))
            throw new ArgumentOutOfRangeException(nameof(ViduSpeechProviderMetadata.Duration), "Vidu text-to-audio duration must be between 2 and 10 seconds.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = "audio1.0",
            ["prompt"] = request.Text,
            ["duration"] = metadata?.Duration,
            ["seed"] = metadata?.Seed,
            ["callback_url"] = metadata?.CallbackUrl
        };

        var json = JsonSerializer.Serialize(payload, ViduSpeechJsonOptions);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, "text2audio")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu text-to-audio request failed ({(int)startResp.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Vidu response missing task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollSpeechCreationsAsync(taskId, ct),
            isTerminal: r => r.State is "success" or "failed",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.State == "failed")
            throw new InvalidOperationException($"Vidu text-to-audio task failed (task_id={taskId}).");

        var creationUrl = TryGetFirstCreationUrl(completed.RawRoot);
        if (string.IsNullOrWhiteSpace(creationUrl))
            throw new InvalidOperationException($"Vidu text-to-audio task completed but returned no creation url (task_id={taskId}).");

        using var fileResp = await _client.GetAsync(creationUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Vidu audio download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessAudioMediaType(creationUrl)
            ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(fileBytes),
                MimeType = mediaType,
                Format = MapMimeToAudioFormat(mediaType)
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.RawRoot.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = startDoc.RootElement.Clone()
            }
        };
    }

    private async Task<SpeechResponse> ViduTextToSpeechAsync(
        SpeechRequest request,
        ViduSpeechProviderMetadata? metadata,
        List<object> warnings,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var voiceId = (request.Voice ?? metadata?.VoiceSettingVoiceId)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Vidu text-to-speech requires a voice ID. Provide SpeechRequest.voice or providerOptions.vidu.voice_setting_voice_id.", nameof(request));

        var speed = request.Speed ?? metadata?.VoiceSettingSpeed;
        if (speed is { } s && (s < 0.5f || s > 2f))
            throw new ArgumentOutOfRangeException(nameof(ViduSpeechProviderMetadata.VoiceSettingSpeed), "Vidu voice_setting_speed must be between 0.5 and 2.0.");

        if (metadata?.VoiceSettingVolume is { } volume && (volume < 0 || volume > 10))
            throw new ArgumentOutOfRangeException(nameof(ViduSpeechProviderMetadata.VoiceSettingVolume), "Vidu voice_setting_volume must be between 0 and 10.");

        if (metadata?.VoiceSettingPitch is { } pitch && (pitch < -12 || pitch > 12))
            throw new ArgumentOutOfRangeException(nameof(ViduSpeechProviderMetadata.VoiceSettingPitch), "Vidu voice_setting_pitch must be between -12 and 12.");

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voice_setting_voice_id"] = voiceId,
            ["voice_setting_speed"] = speed,
            ["voice_setting_volume"] = metadata?.VoiceSettingVolume,
            ["voice_setting_pitch"] = metadata?.VoiceSettingPitch,
            ["voice_setting_emotion"] = metadata?.VoiceSettingEmotion,
            ["payload"] = metadata?.Payload
        };

        var json = JsonSerializer.Serialize(payload, ViduSpeechJsonOptions);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, "audio-tts")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu text-to-speech request failed ({(int)startResp.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var root = startDoc.RootElement;
        var state = root.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString()
            : null;

        if (!string.Equals(state, "success", StringComparison.OrdinalIgnoreCase))
        {
            // API indicates queueing/failed for non-success
            throw new InvalidOperationException($"Vidu text-to-speech did not return success (state={state ?? "unknown"}).");
        }

        var fileUrl = root.TryGetProperty("file_url", out var urlEl) ? urlEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(fileUrl))
            throw new InvalidOperationException("Vidu text-to-speech response missing file_url.");

        using var fileResp = await _client.GetAsync(fileUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Vidu speech download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessAudioMediaType(fileUrl)
            ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(fileBytes),
                MimeType = mediaType,
                Format = MapMimeToAudioFormat(mediaType)
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private async Task<ViduSpeechCreationResult> PollSpeechCreationsAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"tasks/{taskId}/creations");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu task poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var state = root.TryGetProperty("state", out var stateEl)
            ? stateEl.GetString() ?? "unknown"
            : "unknown";

        return new ViduSpeechCreationResult(state, root);
    }

    private static string MapMimeToAudioFormat(string mimeType)
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

    private static string? GuessAudioMediaType(string? url)
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

