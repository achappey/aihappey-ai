using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Freepik;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private const string RunwayGen4TurboPath = "/v1/ai/image-to-video/runway-gen4-turbo";
    private const string Ltx2ProPath = "/v1/ai/text-to-video/ltx-2-pro";
    private const string KlingV26ProPath = "/v1/ai/image-to-video/kling-v2-6-pro";
    private const string KlingV26ProTaskPath = "/v1/ai/image-to-video/kling-v2-6";
    private const string Seedance15Pro480Path = "/v1/ai/video/seedance-1-5-pro-480p";
    private const string Seedance15Pro720Path = "/v1/ai/video/seedance-1-5-pro-720p";
    private const string Seedance15Pro1080Path = "/v1/ai/video/seedance-1-5-pro-1080p";
    private const string SeedancePro480Path = "/v1/ai/image-to-video/seedance-pro-480p";
    private const string SeedancePro720Path = "/v1/ai/image-to-video/seedance-pro-720p";
    private const string SeedancePro1080Path = "/v1/ai/image-to-video/seedance-pro-1080p";
    private const string SeedanceLite480Path = "/v1/ai/image-to-video/seedance-lite-480p";
    private const string SeedanceLite720Path = "/v1/ai/image-to-video/seedance-lite-720p";
    private const string SeedanceLite1080Path = "/v1/ai/image-to-video/seedance-lite-1080p";

    private static readonly JsonSerializerOptions VideoJsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record FreepikVideoTaskResult(string Status, List<string>? Generated, string Raw, string TaskId);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Freepik video generation only supports base64 or data URLs for images.");

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var model = request.Model.Trim();
        var metadata = request.GetProviderMetadata<FreepikVideoProviderMetadata>(GetIdentifier());

        var (endpointPath, taskPath, payload) = BuildVideoRequest(model, request, metadata, warnings);

        var json = JsonSerializer.Serialize(payload, VideoJsonOpts);
        using var startReq = new HttpRequestMessage(HttpMethod.Post, BaseUrl + endpointPath)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var startResp = await _client.SendAsync(startReq, cancellationToken);
        var startRaw = await startResp.Content.ReadAsStringAsync(cancellationToken);
        if (!startResp.IsSuccessStatusCode)
            throw new Exception($"Freepik video start error: {(int)startResp.StatusCode} {startResp.ReasonPhrase}: {startRaw}");

        using var startDoc = JsonDocument.Parse(startRaw);
        var taskId = startDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString();
        if (string.IsNullOrWhiteSpace(taskId))
            throw new Exception("Freepik response missing data.task_id");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => VideoPollAsync(taskPath, taskId, ct),
            isTerminal: r => r.Status is "COMPLETED" or "FAILED",
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.Status == "FAILED")
            throw new Exception($"Freepik video task failed (task_id={taskId}).");

        var firstUrl = final.Generated?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstUrl))
            throw new Exception($"Freepik video task completed but returned no generated URLs (task_id={taskId}).");

        using var fileResp = await _client.GetAsync(firstUrl, cancellationToken);
        var fileBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new Exception($"Freepik video download error: {(int)fileResp.StatusCode} {fileResp.ReasonPhrase}: {err}");
        }

        var mediaType = fileResp.Content.Headers.ContentType?.MediaType
            ?? GuessVideoMediaType(firstUrl)
            ?? "video/mp4";

        using var finalDoc = JsonDocument.Parse(final.Raw);

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
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = finalDoc.RootElement.Clone()
            },
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    task_id = taskId,
                    status = final.Status,
                    generated = final.Generated
                }, JsonSerializerOptions.Web)
            }
        };
    }

    private static (string EndpointPath, string TaskPath, Dictionary<string, object?> Payload) BuildVideoRequest(
        string model,
        VideoRequest request,
        FreepikVideoProviderMetadata? metadata,
        List<object> warnings)
    {
        if (model.Equals("runway-gen4-turbo", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Image is null)
                throw new ArgumentException("Image is required for runway-gen4-turbo.", nameof(request));

            if (request.Fps is not null)
                warnings.Add(new { type = "unsupported", feature = "fps" });

            var payload = new Dictionary<string, object?>
            {
                ["image"] = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? request.Image.Data
                    : request.Image.Data.ToDataUrl(request.Image.MediaType)
            };

            if (!string.IsNullOrWhiteSpace(request.Prompt))
                payload["prompt"] = request.Prompt;

            var ratio = !string.IsNullOrWhiteSpace(request.AspectRatio)
                ? request.AspectRatio
                : request.Resolution;

            if (!string.IsNullOrWhiteSpace(ratio))
                payload["ratio"] = ratio;

            if (!string.IsNullOrWhiteSpace(request.AspectRatio) && !string.IsNullOrWhiteSpace(request.Resolution))
            {
                warnings.Add(new
                {
                    type = "compatibility",
                    feature = "aspect_ratio",
                    details = "Both aspectRatio and resolution provided; using aspectRatio for runway-gen4-turbo ratio."
                });
            }

            if (request.Duration is not null)
                payload["duration"] = request.Duration;

            if (request.Seed is not null)
                payload["seed"] = request.Seed;

            return (RunwayGen4TurboPath, RunwayGen4TurboPath, payload);
        }

        if (model.Equals("ltx-2-pro", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for ltx-2-pro.", nameof(request));

            if (request.Image is not null)
                warnings.Add(new { type = "unsupported", feature = "image" });

            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt
            };

            if (!string.IsNullOrWhiteSpace(request.Resolution))
                payload["resolution"] = request.Resolution;

            if (request.Duration is not null)
                payload["duration"] = request.Duration;

            if (request.Fps is not null)
                payload["fps"] = request.Fps;

            if (request.Seed is not null)
                payload["seed"] = request.Seed;

            if (metadata?.Ltx?.GenerateAudio is not null)
                payload["generate_audio"] = metadata.Ltx.GenerateAudio;

            return (Ltx2ProPath, Ltx2ProPath, payload);
        }

        if (model.Equals("kling-v2-6-pro", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Fps is not null)
                warnings.Add(new { type = "unsupported", feature = "fps" });

            var payload = new Dictionary<string, object?>();

            if (!string.IsNullOrWhiteSpace(request.Prompt))
                payload["prompt"] = request.Prompt;

            if (request.Image is not null)
            {
                payload["image"] = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? request.Image.Data
                    : request.Image.Data.ToDataUrl(request.Image.MediaType);
            }

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspect_ratio"] = request.AspectRatio;

            if (request.Duration is not null)
                payload["duration"] = request.Duration.ToString();

            if (metadata?.Kling?.GenerateAudio is not null)
                payload["generate_audio"] = metadata.Kling.GenerateAudio;

            if (!string.IsNullOrWhiteSpace(metadata?.Kling?.NegativePrompt))
                payload["negative_prompt"] = metadata.Kling.NegativePrompt;

            if (metadata?.Kling?.CfgScale is { } cfgScale)
                payload["cfg_scale"] = cfgScale;

            return (KlingV26ProPath, KlingV26ProTaskPath, payload);
        }

        if (model.Equals("seedance-1-5-pro-480p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-1-5-pro-720p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-1-5-pro-1080p", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for seedance-1-5-pro.", nameof(request));

            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt
            };

            if (request.Image is not null)
            {
                payload["image"] = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? request.Image.Data
                    : request.Image.Data.ToDataUrl(request.Image.MediaType);
            }

            if (request.Duration is not null)
                payload["duration"] = request.Duration;

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspect_ratio"] = request.AspectRatio;

            if (request.Seed is not null)
                payload["seed"] = request.Seed;

            if (request.Fps is not null)
                warnings.Add(new { type = "unsupported", feature = "fps" });

            var endpointPath = model.Equals("seedance-1-5-pro-480p", StringComparison.OrdinalIgnoreCase)
                ? Seedance15Pro480Path
                : model.Equals("seedance-1-5-pro-720p", StringComparison.OrdinalIgnoreCase)
                    ? Seedance15Pro720Path
                    : Seedance15Pro1080Path;

            return (endpointPath, endpointPath, payload);
        }

        if (model.Equals("seedance-pro-480p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-pro-720p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-pro-1080p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-lite-480p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-lite-720p", StringComparison.OrdinalIgnoreCase)
            || model.Equals("seedance-lite-1080p", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Image is null)
                throw new ArgumentException("Image is required for Seedance Pro/Lite.", nameof(request));

            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for Seedance Pro/Lite.", nameof(request));

            var payload = new Dictionary<string, object?>
            {
                ["prompt"] = request.Prompt,
                ["image"] = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? request.Image.Data
                    : request.Image.Data.ToDataUrl(request.Image.MediaType)
            };

            if (request.Duration is not null)
                payload["duration"] = request.Duration.ToString();

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                payload["aspect_ratio"] = request.AspectRatio;

            if (request.Seed is not null)
                payload["seed"] = request.Seed;

            if (request.Fps is not null)
                payload["frames_per_second"] = request.Fps;

            var endpointPath = model.Equals("seedance-pro-480p", StringComparison.OrdinalIgnoreCase)
                ? SeedancePro480Path
                : model.Equals("seedance-pro-720p", StringComparison.OrdinalIgnoreCase)
                    ? SeedancePro720Path
                    : model.Equals("seedance-pro-1080p", StringComparison.OrdinalIgnoreCase)
                        ? SeedancePro1080Path
                        : model.Equals("seedance-lite-480p", StringComparison.OrdinalIgnoreCase)
                            ? SeedanceLite480Path
                            : model.Equals("seedance-lite-720p", StringComparison.OrdinalIgnoreCase)
                                ? SeedanceLite720Path
                                : SeedanceLite1080Path;

            return (endpointPath, endpointPath, payload);
        }

        throw new NotSupportedException($"Freepik video model '{request.Model}' is not supported.");
    }

    private async Task<FreepikVideoTaskResult> VideoPollAsync(string taskPath, string taskId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}{taskPath}/{taskId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Freepik video poll error: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

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

        return new FreepikVideoTaskResult(status, generated, raw, returnedTaskId);
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
