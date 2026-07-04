using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Zai;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider 
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
        var isViduStartEnd = string.Equals(model, "viduq1-start-end", StringComparison.OrdinalIgnoreCase)
            || string.Equals(model, "vidu2-start-end", StringComparison.OrdinalIgnoreCase);
        var isViduReference = string.Equals(model, "vidu2-reference", StringComparison.OrdinalIgnoreCase);

        if (isViduImage && request.Image is null)
            throw new InvalidOperationException("Z.AI video generation requires an image for Vidu image models.");

        if (isViduStartEnd && request.FrameImages?.Any() != true)
            throw new InvalidOperationException("Z.AI video generation requires frameImages for Vidu first-and-last-frame models.");

        if (isViduReference && request.InputReferences?.Any() != true)
            throw new InvalidOperationException("Z.AI video generation requires inputReferences for Vidu reference models.");

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

        if (request.FrameImages?.Any() == true && !isViduStartEnd && !isCogVideo)
        {
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
        }

        if (request.InputReferences?.Any() == true && !isViduReference)
        {
            warnings.Add(new { type = "unsupported", feature = "inputReferences" });
        }

        if (!isViduText && !isViduReference && !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
        }

        var metadata = GetVideoProviderMetadata<ZaiVideoProviderMetadata>(request, GetIdentifier());

        var payload = BuildVideoPayload(request, metadata, isCogVideo, isViduText, isViduImage, isViduStartEnd, isViduReference, warnings);
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
        bool isViduStartEnd,
        bool isViduReference,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["prompt"] = request.Prompt;

        AddZaiVideoImageInputs(payload, request, isCogVideo, isViduImage, isViduStartEnd, isViduReference);

        var duration = request.Duration;
        if (duration is not null)
            payload["duration"] = duration;

        var size = request.Resolution;
        if (!string.IsNullOrWhiteSpace(size))
            payload["size"] = size;

        var aspectRatio = request.AspectRatio;
        if ((isViduText || isViduReference) && !string.IsNullOrWhiteSpace(aspectRatio))
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
        else if (isViduImage || isViduStartEnd || isViduReference)
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

    private static void AddZaiVideoImageInputs(
        Dictionary<string, object?> payload,
        VideoRequest request,
        bool isCogVideo,
        bool isViduImage,
        bool isViduStartEnd,
        bool isViduReference)
    {
        if (isViduReference)
        {
            var references = (request.InputReferences ?? [])
                .Select(ToZaiVideoImageUrl)
                .ToList();

            if (references.Count is < 1 or > 3)
                throw new InvalidOperationException("Z.AI Vidu reference video generation requires 1 to 3 inputReferences images.");

            payload["image_url"] = references;
            return;
        }

        if (isViduStartEnd)
        {
            var frameImageUrls = GetZaiStartEndFrameImageUrls(request);
            if (frameImageUrls.Count is < 1 or > 2)
                throw new InvalidOperationException("Z.AI Vidu first-and-last-frame video generation requires 1 or 2 frameImages images.");

            payload["image_url"] = frameImageUrls;
            return;
        }

        if (isCogVideo)
        {
            var frameImages = request.FrameImages?.ToList() ?? [];
            if (frameImages.Count > 0)
            {
                var frameImageUrls = GetZaiStartEndFrameImageUrls(request);
                if (frameImageUrls.Count > 2)
                    throw new InvalidOperationException("Z.AI CogVideoX video generation supports at most 2 frameImages images.");

                payload["image_url"] = frameImageUrls;
                return;
            }

            if (request.Image is not null)
                payload["image_url"] = new[] { ToZaiVideoImageUrl(request.Image) };

            return;
        }

        if (isViduImage && request.Image is not null)
            payload["image_url"] = ToZaiVideoImageUrl(request.Image);
    }

    private static List<string> GetZaiStartEndFrameImageUrls(VideoRequest request)
    {
        VideoFile? firstFrame = null;
        VideoFile? lastFrame = null;

        foreach (var frameImage in request.FrameImages ?? [])
        {
            if (frameImage?.Image is null)
                throw new InvalidOperationException("Z.AI video frameImages entries must include an image.");

            if (IsFirstFrame(frameImage.FrameType))
            {
                if (firstFrame is not null)
                    throw new InvalidOperationException("Z.AI video generation supports only one first_frame image.");

                firstFrame = frameImage.Image;
            }
            else if (IsLastFrame(frameImage.FrameType))
            {
                if (lastFrame is not null)
                    throw new InvalidOperationException("Z.AI video generation supports only one last_frame image.");

                lastFrame = frameImage.Image;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported Z.AI video frameType '{frameImage.FrameType}'. Use 'first_frame' or 'last_frame'.");
            }
        }

        List<string> imageUrls = [];
        if (firstFrame is not null)
            imageUrls.Add(ToZaiVideoImageUrl(firstFrame));
        if (lastFrame is not null)
            imageUrls.Add(ToZaiVideoImageUrl(lastFrame));

        return imageUrls;
    }

    private static string ToZaiVideoImageUrl(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.Data))
            throw new InvalidOperationException("Z.AI video image data is required.");

        var data = image.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        if (string.IsNullOrWhiteSpace(image.MediaType))
            throw new InvalidOperationException("Z.AI video image mediaType is required for raw base64 image data.");

        return data.ToDataUrl(image.MediaType);
    }

    private static bool IsFirstFrame(string? frameType)
        => string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase);

    private static bool IsLastFrame(string? frameType)
        => string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase);

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
