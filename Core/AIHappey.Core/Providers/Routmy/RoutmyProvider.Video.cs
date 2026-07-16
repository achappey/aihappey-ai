using System.Net.Mime;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Routmy;

public partial class RoutmyProvider
{
    private async Task<VideoResponse> VideoRequestRoutmy(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var payload = BuildRoutmyVideoPayload(request);
        var timeout = ResolveRoutmyVideoTimeout(request.ProviderOptions);
        var root = await SendRoutmyMediaJsonAsync("v1/video/generations", payload, "video", cancellationToken, timeout);
        var videos = await ExtractRoutmyVideosAsync(root, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException("Routmy video generation returned no videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = BuildRoutmyMediaProviderMetadata(payload, root),
            Response = new()
            {
                Timestamp = ResolveRoutmyCreatedTimestamp(root) ?? now,
                ModelId = ResolveRoutmyResponseModel(root, request.Model).ToModelId(GetIdentifier())
            }
        };
    }

    private static Dictionary<string, object?> BuildRoutmyVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.N is not null)
            payload["n"] = request.N;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (request.Fps is not null)
            payload["fps"] = request.Fps;

        if (request.Image is not null)
            payload["input_image"] = ToRoutmyMediaValue(request.Image, MediaTypeNames.Image.Png);

        AddRoutmyFrameImages(payload, request.FrameImages);
        AddRoutmyInputReferences(payload, request.InputReferences);
        MergeRoutmyProviderOptions(payload, request.ProviderOptions, RoutmyVideoProtectedKeys);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        return payload;
    }

    private async Task<List<VideoResponseFile>> ExtractRoutmyVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        await ExtractRoutmyVideoItemsAsync(root, "videos", videos, cancellationToken);
        if (videos.Count == 0)
            await ExtractRoutmyVideoItemsAsync(root, "data", videos, cancellationToken);

        return videos;
    }

    private async Task ExtractRoutmyVideoItemsAsync(
        JsonElement root,
        string propertyName,
        List<VideoResponseFile> videos,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in items.EnumerateArray())
        {
            var video = await ExtractRoutmyVideoItemAsync(item, cancellationToken);
            if (video is not null)
                videos.Add(video);
        }
    }

    private async Task<VideoResponseFile?> ExtractRoutmyVideoItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var b64 = TryGetRoutmyString(item, "b64_json")
            ?? TryGetRoutmyString(item, "base64")
            ?? TryGetRoutmyString(item, "data");

        if (!string.IsNullOrWhiteSpace(b64))
        {
            var mediaType = TryGetRoutmyString(item, "mime_type")
                ?? TryGetRoutmyString(item, "mimeType")
                ?? "video/mp4";

            return new VideoResponseFile
            {
                Type = "base64",
                Data = StripRoutmyDataUrlPrefix(b64),
                MediaType = mediaType
            };
        }

        var url = TryGetRoutmyString(item, "url")
            ?? TryGetRoutmyNestedString(item, "video_url", "url");

        if (string.IsNullOrWhiteSpace(url))
            return null;

        var downloaded = await TryFetchRoutmyAsBase64Async(url, cancellationToken);
        if (downloaded is not null)
        {
            return new VideoResponseFile
            {
                Type = "base64",
                Data = downloaded.Value.Base64,
                MediaType = downloaded.Value.MediaType
            };
        }

        return new VideoResponseFile
        {
            Type = "base64",
            Data = url,
            MediaType = TryGetRoutmyString(item, "mime_type")
                ?? TryGetRoutmyString(item, "mimeType")
                ?? GuessRoutmyMediaTypeFromUrl(url, "video/mp4")
        };
    }

    private static void AddRoutmyFrameImages(Dictionary<string, object?> payload, IEnumerable<VideoFrameImage>? frameImages)
    {
        if (frameImages is null)
            return;

        foreach (var frame in frameImages)
        {
            if (frame?.Image is null || string.IsNullOrWhiteSpace(frame.FrameType))
                continue;

            var key = frame.FrameType switch
            {
                "last_frame" => "last_frame_image",
                "lastFrame" => "last_frame_image",
                "last_frame_image" => "last_frame_image",
                _ => "input_image"
            };

            payload[key] = ToRoutmyMediaValue(frame.Image, MediaTypeNames.Image.Png);
        }
    }

    private static void AddRoutmyInputReferences(Dictionary<string, object?> payload, IEnumerable<VideoFile>? inputReferences)
    {
        var references = inputReferences?.Where(reference => reference is not null).ToArray();
        if (references is null || references.Length == 0)
            return;

        var imageReferences = new List<string>();
        var videoReferences = new List<string>();
        var audioReferences = new List<string>();

        foreach (var reference in references)
        {
            var mediaType = reference.MediaType ?? string.Empty;
            var value = ToRoutmyMediaValue(reference, MediaTypeNames.Application.Octet);

            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                imageReferences.Add(value);
            else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                audioReferences.Add(value);
            else
                videoReferences.Add(value);
        }

        if (imageReferences.Count == 1 && !payload.ContainsKey("input_image"))
            payload["input_image"] = imageReferences[0];
        else if (imageReferences.Count > 0)
            payload["reference_images"] = imageReferences;

        if (videoReferences.Count == 1)
            payload["input_video"] = videoReferences[0];
        else if (videoReferences.Count > 0)
            payload["reference_videos"] = videoReferences;

        if (audioReferences.Count > 0)
            payload["input_audio"] = audioReferences[0];
    }

    private static TimeSpan? ResolveRoutmyVideoTimeout(Dictionary<string, JsonElement>? providerOptions)
    {
        var metadata = TryGetRoutmyProviderOptions(providerOptions);
        if (metadata is null)
            return TimeSpan.FromMinutes(15);

        var timeoutMs = TryGetRoutmyInt(metadata.Value, "poll_timeout_ms")
            ?? TryGetRoutmyInt(metadata.Value, "pollTimeoutMs");

        return timeoutMs is > 0
            ? TimeSpan.FromMilliseconds(timeoutMs.Value)
            : TimeSpan.FromMinutes(15);
    }

    private static string ToRoutmyMediaValue(VideoFile file, string fallbackMediaType)
    {
        var value = file.Data;
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType) ? fallbackMediaType : file.MediaType;
        return $"data:{mediaType};base64,{value}";
    }

    private static string StripRoutmyDataUrlPrefix(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        var comma = value.IndexOf(',');
        return comma >= 0 ? value[(comma + 1)..] : value;
    }

    private static readonly HashSet<string> RoutmyVideoProtectedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "model",
        "prompt"
    };
}
