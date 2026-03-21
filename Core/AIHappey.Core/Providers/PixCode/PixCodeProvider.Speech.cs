using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PixCode;

public partial class PixCodeProvider
{
    private static readonly JsonSerializerOptions PixCodeSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<SpeechResponse> SpeechRequestPixCode(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Voice) && request.ProviderOptions is null)
            warnings.Add(new { type = "info", feature = "voice", details = "Voice is forwarded as voice_setting.voice_id." });

        var payload = BuildPixCodeSpeechPayload(request);
        var payloadJson = JsonSerializer.Serialize(payload, PixCodeSpeechJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PixCode speech submit failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var taskId = TryGetString(createDoc.RootElement, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("PixCode speech submit returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/audio/speech/{taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"PixCode speech status failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            root => IsTerminalStatus(TryGetString(root, "status")),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetString(completed, "status");
        if (!IsSuccessStatus(finalStatus))
            throw new InvalidOperationException($"PixCode speech generation failed with status '{finalStatus ?? "unknown"}' (task_id={taskId}). Response: {completed.GetRawText()}");

        var audioUrl = TryGetPixCodeSpeechAudioUrl(completed);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException($"PixCode speech task completed but returned no audio url (task_id={taskId}).");

        var audioBytes = await _client.GetByteArrayAsync(audioUrl, cancellationToken);
        var audioFormat = ResolvePixCodeSpeechFormat(completed, request.OutputFormat);
        var mimeType = ResolvePixCodeSpeechMimeType(completed, audioUrl, audioFormat) ?? "audio/mpeg";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                family = "speech-task",
                submit = createDoc.RootElement.Clone(),
                poll = completed
            }, JsonSerializerOptions.Web)
        };

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mimeType,
                Format = audioFormat
            },
            ProviderMetadata = providerMetadata,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed
            }
        };
    }

    private Dictionary<string, object?> BuildPixCodeSpeechPayload(SpeechRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["text"] = request.Text
        };

        if (!string.IsNullOrWhiteSpace(request.Language))
            payload["language_boost"] = request.Language;

        var voiceSetting = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(request.Voice))
            voiceSetting["voice_id"] = request.Voice;
        if (request.Speed is not null)
            voiceSetting["speed"] = request.Speed;

        if (voiceSetting.Count > 0)
            payload["voice_setting"] = voiceSetting;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
        {
            payload["audio_setting"] = new Dictionary<string, object?>
            {
                ["format"] = request.OutputFormat
            };
        }

        var providerOptions = GetPixCodeSpeechProviderOptions(request);
        if (providerOptions.HasValue && providerOptions.Value.ValueKind == JsonValueKind.Object)
            MergePixCodePayload(payload, providerOptions.Value);

        return payload;
    }

    private static JsonElement? GetPixCodeSpeechProviderOptions(SpeechRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue(nameof(PixCode).ToLowerInvariant(), out var options))
            return null;

        return options.ValueKind == JsonValueKind.Object
            ? options.Clone()
            : null;
    }

    private static void MergePixCodePayload(Dictionary<string, object?> target, JsonElement source)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && target.TryGetValue(property.Name, out var existing)
                && existing is Dictionary<string, object?> existingObject)
            {
                MergeObjectValues(existingObject, property.Value);
                continue;
            }

            target[property.Name] = property.Value.Clone();
        }
    }

    private static string? TryGetPixCodeSpeechAudioUrl(JsonElement root)
    {
        if (TryGetString(root, "audio_url") is { } directUrl && !string.IsNullOrWhiteSpace(directUrl))
            return directUrl;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(data, "audio_url") is { } nestedUrl && !string.IsNullOrWhiteSpace(nestedUrl))
                return nestedUrl;
        }

        return null;
    }

    private static string ResolvePixCodeSpeechFormat(JsonElement root, string? requestedFormat)
    {
        if (TryGetString(root, "audio_type") is { } responseFormat && !string.IsNullOrWhiteSpace(responseFormat))
            return responseFormat;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(data, "audio_type") is { } nestedFormat && !string.IsNullOrWhiteSpace(nestedFormat))
                return nestedFormat;
        }

        if (!string.IsNullOrWhiteSpace(requestedFormat))
            return requestedFormat;

        return "mp3";
    }

    private static string? ResolvePixCodeSpeechMimeType(JsonElement root, string? audioUrl, string? format)
    {
        var audioType = TryGetString(root, "audio_type");
        if (string.IsNullOrWhiteSpace(audioType) && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            audioType = TryGetString(data, "audio_type");

        var normalized = (audioType ?? format ?? audioUrl ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return normalized;

        if (normalized.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || normalized.Equals("wav", StringComparison.OrdinalIgnoreCase))
            return "audio/wav";

        if (normalized.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) || normalized.Equals("flac", StringComparison.OrdinalIgnoreCase))
            return "audio/flac";

        if (normalized.EndsWith(".pcm", StringComparison.OrdinalIgnoreCase) || normalized.Equals("pcm", StringComparison.OrdinalIgnoreCase))
            return "audio/L16";

        if (normalized.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || normalized.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            return "audio/mpeg";

        return null;
    }
}
