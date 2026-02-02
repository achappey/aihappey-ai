using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Zai;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider : IModelProvider
{
    private static readonly JsonSerializerOptions ZaiVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var model = request.Model;
        var isCogVideo = string.Equals(model, "cogvideox-3", StringComparison.OrdinalIgnoreCase);
        var isViduText = string.Equals(model, "viduq1-text", StringComparison.OrdinalIgnoreCase);
        var isViduImage = string.Equals(model, "viduq1-image", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "vidu2-image", StringComparison.OrdinalIgnoreCase);

        if (isViduImage && request.Image is null)
            throw new InvalidOperationException("Z.AI video generation requires an image for Vidu image models.");

        if (request.Seed is not null)
        {
            warnings.Add(new { type = "unsupported", feature = "seed" });
        }

        if (request.N is not null && request.N > 1)
        {
            warnings.Add(new { type = "unsupported", feature = "n" });
        }

        if (request.Fps is not null && !isCogVideo)
        {
            warnings.Add(new { type = "unsupported", feature = "fps" });
        }

        if (request.Image is not null && !isViduImage && !isCogVideo)
        {
            warnings.Add(new { type = "unsupported", feature = "image" });
        }

        if (!isViduText && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        }

        var metadata = GetVideoProviderMetadata<ZaiVideoProviderMetadata>(request, GetIdentifier());

        var payload = BuildVideoPayload(request, metadata, isCogVideo, isViduText, isViduImage, warnings);
        var json = JsonSerializer.Serialize(payload, ZaiVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v4/videos/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Z.AI video generation failed ({(int)createResp.StatusCode})"
                : $"Z.AI video generation failed ({(int)createResp.StatusCode}): {createRaw}");
        }

        using var createDoc = JsonDocument.Parse(createRaw);
        var root = createDoc.RootElement;
        var taskId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Z.AI video generation returned no id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v4/async-result/{taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Z.AI async-result failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return (root: pollDoc.RootElement.Clone(), raw: pollRaw);
            },
            result =>
            {
                var status = TryGetStatus(result.root);
                return string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "FAIL", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var status = TryGetStatus(completed.root);
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Z.AI video generation failed with status '{status}'.");
        }

        var videoUrl = TryGetFirstVideoUrl(completed.root);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Z.AI video result contained no video url.");

        var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
        var mediaType = GuessVideoMediaType(videoUrl) ?? "video/mp4";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["zai"] = completed.root.Clone()
        };

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = createRaw
            }
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(
        VideoRequest request,
        ZaiVideoProviderMetadata? metadata,
        bool isCogVideo,
        bool isViduText,
        bool isViduImage,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        if (request.Image is not null)
        {
            if (request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Z.AI video generation only supports base64 or data URLs for images.");

            var imageData = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? request.Image.Data
                : request.Image.Data.ToDataUrl(request.Image.MediaType);

            payload["image_url"] = isCogVideo ? new[] { imageData } : imageData;
        }

        var duration = request.Duration;
        if (duration is not null)
            payload["duration"] = duration;

        var size = request.Resolution;
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        var aspectRatio = request.AspectRatio;
        if (isViduText && !string.IsNullOrWhiteSpace(aspectRatio))
            payload["aspect_ratio"] = aspectRatio;

        if (isCogVideo)
        {
            var quality = metadata?.Quality;
            if (!string.IsNullOrWhiteSpace(quality))
                payload["quality"] = quality;

            var fps = request.Fps;
            if (fps is not null)
                payload["fps"] = fps;

            if (metadata?.WithAudio is not null)
                payload["with_audio"] = metadata.WithAudio;
        }
        else if (isViduText)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.Style))
                payload["style"] = metadata!.Style;

            if (!string.IsNullOrWhiteSpace(metadata?.MovementAmplitude))
                payload["movement_amplitude"] = metadata!.MovementAmplitude;
        }
        else if (isViduImage)
        {
            if (!string.IsNullOrWhiteSpace(metadata?.MovementAmplitude))
                payload["movement_amplitude"] = metadata!.MovementAmplitude;

            if (metadata?.WithAudio is not null)
                payload["with_audio"] = metadata.WithAudio;
        }
        else
        {
            warnings.Add(new { type = "unsupported", feature = "model" });
        }

        return payload;
    }

    private static string? TryGetStatus(JsonElement root)
    {
        return root.TryGetProperty("task_status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;
    }

    private static string? TryGetFirstVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("video_result", out var videoResult)
            && videoResult.ValueKind == JsonValueKind.Array)
        {
            var first = videoResult.EnumerateArray().FirstOrDefault();
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
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
