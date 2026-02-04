using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Vidu;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vidu;

public partial class ViduProvider
{
    private static readonly JsonSerializerOptions ViduVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ViduVideoCreationResult(string State, JsonElement RawRoot);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt);
        var hasImage = request.Image is not null;

        if (!hasPrompt && !hasImage)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var videoMetadata = request.GetProviderMetadata<ViduVideoProviderMetadata>(GetIdentifier());
        var (endpoint, payload) = BuildViduVideoPayload(request, videoMetadata, warnings);

        var json = JsonSerializer.Serialize(payload, ViduVideoJsonOptions);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vidu video request failed ({(int)startResp.StatusCode}): {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.TryGetProperty("task_id", out var taskEl)
            ? taskEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Vidu response missing task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollCreationsAsync(taskId, ct),
            isTerminal: r => r.State is "success" or "failed",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.State == "failed")
            throw new InvalidOperationException($"Vidu video task failed (task_id={taskId}).");

        var creationUrl = TryGetFirstCreationUrl(completed.RawRoot);
        if (string.IsNullOrWhiteSpace(creationUrl))
            throw new InvalidOperationException($"Vidu video task completed but returned no creation url (task_id={taskId}).");

        using var fileResp = await _client.GetAsync(creationUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Vidu video download failed ({(int)fileResp.StatusCode}): {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(creationUrl)
            ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(fileBytes)
                }
            ],
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

    private static (string Endpoint, Dictionary<string, object?> Payload) BuildViduVideoPayload(
        VideoRequest request,
        ViduVideoProviderMetadata? metadata,
        List<object> warnings)
    {
        var hasImage = request.Image is not null;
        var endpoint = hasImage ? "img2video" : "text2video";

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (hasImage)
        {
            var image = request.Image!;
            var imageData = image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? image.Data
                : image.Data.ToDataUrl(image.MediaType);

            payload["images"] = new[] { imageData };

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

            if (!string.IsNullOrWhiteSpace(metadata?.Style))
                warnings.Add(new { type = "unsupported", feature = "style" });

            if (!string.IsNullOrWhiteSpace(metadata?.MovementAmplitude))
                payload["movement_amplitude"] = metadata!.MovementAmplitude;

            if (metadata?.Audio is not null)
                payload["audio"] = metadata.Audio;

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId))
                payload["voice_id"] = metadata!.VoiceId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for text-to-video.", nameof(request));

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspect_ratio"] = request.AspectRatio;

            if (!string.IsNullOrWhiteSpace(metadata?.Style))
                payload["style"] = metadata!.Style;

            if (!string.IsNullOrWhiteSpace(metadata?.MovementAmplitude))
                payload["movement_amplitude"] = metadata!.MovementAmplitude;

            if (metadata?.Audio is not null)
                payload["audio"] = metadata.Audio;

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId))
                warnings.Add(new { type = "unsupported", feature = "voice_id" });
        }

        if (metadata?.Bgm is not null)
            payload["bgm"] = metadata.Bgm;

        if (metadata?.OffPeak is not null)
            payload["off_peak"] = metadata.OffPeak;

        if (!string.IsNullOrWhiteSpace(metadata?.Payload))
            payload["payload"] = metadata.Payload;

        return (endpoint, payload);
    }

    private async Task<ViduVideoCreationResult> PollCreationsAsync(string taskId, CancellationToken cancellationToken)
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

        return new ViduVideoCreationResult(state, root);
    }

    private static string? TryGetFirstCreationUrl(JsonElement root)
    {
        if (root.TryGetProperty("creations", out var creations)
            && creations.ValueKind == JsonValueKind.Array)
        {
            var first = creations.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String)
            {
                return urlEl.GetString();
            }
        }

        return null;
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}

