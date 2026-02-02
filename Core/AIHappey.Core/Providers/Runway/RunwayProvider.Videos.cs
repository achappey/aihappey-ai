using AIHappey.Common.Extensions;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    private static readonly JsonSerializerOptions VideoJsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "fps"
            });
        }

        if (request.N is not null && request.N > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n"
            });
        }

        var hasInput = request.Image is not null;
        var mediaType = request.Image?.MediaType ?? string.Empty;
        var isImage = hasInput && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var isVideo = hasInput && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

        var payload = BuildVideoPayload(request, isImage, isVideo, warnings);
        var endpoint = ResolveVideoEndpoint(isImage, isVideo);

        var json = JsonSerializer.Serialize(payload, VideoJsonOpts);
        using var resp = await _client.PostAsync(
            endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway video request failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = resolvedMime,
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static string ResolveVideoEndpoint(bool isImage, bool isVideo)
    {
        if (isVideo)
            return "v1/video_to_video";

        if (isImage)
            return "v1/image_to_video";

        return "v1/text_to_video";
    }

    private static Dictionary<string, object?> BuildVideoPayload(
        VideoRequest request,
        bool isImage,
        bool isVideo,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["promptText"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["ratio"] = request.Resolution;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.Seed is not null)
        {
            if (isImage || isVideo)
            {
                payload["seed"] = request.Seed;
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "seed" });
            }
        }

        if (request.Image is not null)
        {
            var dataUri = request.Image.Data.ToDataUrl(request.Image.MediaType);

            if (isVideo)
            {
                payload["videoUri"] = dataUri;
            }
            else if (isImage)
            {
                payload["promptImage"] = dataUri;
            }
            else
            {
                throw new ArgumentException($"Unsupported mediaType '{request.Image.MediaType}'. Expected image/* or video/*.", nameof(request));
            }
        }

        return payload;
    }
}
