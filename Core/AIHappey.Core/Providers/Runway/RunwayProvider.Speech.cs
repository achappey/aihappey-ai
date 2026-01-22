using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.Runway;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    private const string RunwayPresetVoiceType = "runway-preset";
    private const string RunwaySoundEffectsModel = "eleven_text_to_sound_v2";

    private async Task<SpeechResponse> RunwayTextToSpeechAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Runway TTS currently only accepts promptText + voice preset; other unified knobs do not map.
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var metadata = request.GetSpeechProviderMetadata<RunwaySpeechProviderMetadata>(GetIdentifier());

        // Required: presetId
        var presetId = (request.Voice ?? metadata?.Voice?.PresetId)?.Trim();
        if (string.IsNullOrWhiteSpace(presetId))
            throw new ArgumentException(
                "Runway Text-to-Speech requires a voice presetId. Provide SpeechRequest.voice or providerOptions.runway.voice.",
                nameof(request));

        var payload = new
        {
            model = request.Model,
            promptText = request.Text,
            voice = new
            {
                type = RunwayPresetVoiceType,
                presetId
            }
        };

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync(
            "v1/text_to_speech",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway TTS failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var base64 = Convert.ToBase64String(bytes);

        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = resolvedMime,
                Format = MapMimeToAudioFormat(resolvedMime)
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = node
            }
        };
    }

    private async Task<SpeechResponse> RunwaySoundEffectAsync(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required (used as promptText).", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        // Sound effects are prompt-based; other unified knobs do not map.
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

        var metadata = request.GetSpeechProviderMetadata<RunwaySpeechProviderMetadata>(GetIdentifier());
        var se = metadata?.SoundEffects;

        if (se?.Duration is { } duration)
        {
            if (duration < 0.5 || duration > 30)
                throw new ArgumentOutOfRangeException(nameof(RunwaySpeechProviderMetadata.SoundEffects), "soundEffects.duration must be between 0.5 and 30 seconds.");
        }

        var payload = new Dictionary<string, object?>
        {
            // Runway requires a fixed model id for this endpoint.
            ["model"] = RunwaySoundEffectsModel,
            ["promptText"] = request.Text,
        };

        if(se?.Duration is not null) 
            payload["duration"] = se?.Duration;

        if(se?.Loop is not null) 
            payload["loop"] = se?.Loop;

        var json = JsonSerializer.Serialize(payload, JsonOpts);
        using var resp = await _client.PostAsync(
            "v1/sound_effect",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway sound_effect failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var base64 = Convert.ToBase64String(bytes);

        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64,
                MimeType = resolvedMime,
                Format = MapMimeToAudioFormat(resolvedMime)
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["runway"] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    output_url = outputUrl
                }, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = node
            }
        };
    }

    private async Task<(byte[] Bytes, string? MimeType, string? OutputUrl)> WaitForTaskAndDownloadFirstOutputAsync(
        string taskId,
        CancellationToken ct = default)
    {
        string? status;
        JsonNode? json;

        do
        {
            using var resp = await _client.GetAsync($"v1/tasks/{taskId}", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"{resp.StatusCode}: {text}");

            json = JsonNode.Parse(text);
            status = json?["status"]?.ToString();

            if (status == "SUCCEEDED")
            {
                var outputUrl = ExtractFirstOutputUrl(json);
                if (string.IsNullOrWhiteSpace(outputUrl))
                    throw new Exception("No outputs returned by Runway API.");

                using var outResp = await _client.GetAsync(outputUrl, ct);
                var bytes = await outResp.Content.ReadAsByteArrayAsync(ct);

                if (!outResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Runway output download failed ({(int)outResp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

                var mime = outResp.Content.Headers.ContentType?.MediaType;
                return (bytes, mime, outputUrl);
            }

            if (status == "FAILED")
            {
                var reason = json?["failure"]?.ToString() ?? "Unknown failure.";
                var code = json?["failureCode"]?.ToString() ?? "";
                throw new Exception($"Runway task failed: {reason} ({code})");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        } while (status != "SUCCEEDED" && status != "FAILED");

        throw new TimeoutException($"Runway task {taskId} did not complete.");
    }

    private static string? ExtractFirstOutputUrl(JsonNode? taskJson)
    {
        var output = taskJson?["output"];
        if (output is null)
            return null;

        // Most commonly: output: ["https://..."]
        if (output is JsonArray arr && arr.Count > 0)
        {
            var first = arr[0];
            if (first is null)
                return null;

            // Sometimes: output: [{"uri":"https://..."}]
            var uri = first?["uri"]?.ToString();
            if (!string.IsNullOrWhiteSpace(uri))
                return uri;

            // Fallback to stringification
            var s = first.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        // Rare: output: "https://..."
        var direct = output.ToString();
        return string.IsNullOrWhiteSpace(direct) ? null : direct;
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

    private static string? GuessMimeFromUrl(string? url)
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

