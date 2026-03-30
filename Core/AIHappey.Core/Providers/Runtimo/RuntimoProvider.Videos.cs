using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runtimo;

public partial class RuntimoProvider
{
    private async Task<VideoResponse> RuntimoVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution", details = "Pass model-specific resolution through providerOptions.runtimo.input." });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio", details = "Pass model-specific aspect ratio through providerOptions.runtimo.input." });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Pass model-specific seed through providerOptions.runtimo.input." });

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration", details = "Pass model-specific duration through providerOptions.runtimo.input." });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps", details = "Pass model-specific fps through providerOptions.runtimo.input." });

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Pass model-specific video count through providerOptions.runtimo.input." });

        var modelPath = NormalizeModelPath(request.Model, GetIdentifier());
        var endpoint = $"v1/models/{modelPath}/call";
        var metadata = GetVideoProviderMetadata(request, GetIdentifier());
        var payload = BuildRuntimoPayload(request.Prompt, NormalizeVideoInput(request.Image), metadata);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, RuntimoMediaJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runtimo video request failed ({(int)response.StatusCode}) [{endpoint}]: {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        var videos = await ExtractRuntimoVideosAsync(root, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException("Runtimo video response did not contain any videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = BuildRuntimoProviderMetadata(endpoint, root),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint,
                    body = root
                }
            }
        };
    }

    private async Task<List<VideoResponseFile>> ExtractRuntimoVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        if (TryGetPropertyIgnoreCase(root, "videos", out var videosElement) && videosElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in videosElement.EnumerateArray())
            {
                var video = await NormalizeVideoOutputAsync(item, cancellationToken);
                if (video is not null)
                    videos.Add(video);
            }
        }

        if (videos.Count == 0 && TryGetPropertyIgnoreCase(root, "video", out var videoElement))
        {
            var video = await NormalizeVideoOutputAsync(videoElement, cancellationToken);
            if (video is not null)
                videos.Add(video);
        }

        if (videos.Count == 0)
        {
            var url = GetString(root, "url", "video_url", "videoUrl", "output_url", "outputUrl", "download_url", "downloadUrl", "file_url", "fileUrl");
            if (!string.IsNullOrWhiteSpace(url))
            {
                var mediaType = GetString(root, "content_type", "media_type", "mime_type", "mimeType");
                var video = await NormalizeVideoOutputAsync(url, mediaType, cancellationToken);
                if (video is not null)
                    videos.Add(video);
            }
        }

        return videos;
    }

    private async Task<VideoResponseFile?> NormalizeVideoOutputAsync(JsonElement element, CancellationToken cancellationToken)
    {
        if (element.ValueKind == JsonValueKind.String)
            return await NormalizeVideoOutputAsync(element.GetString(), null, cancellationToken);

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var mediaType = GetString(element, "content_type", "media_type", "mime_type", "mimeType");

        var url = GetString(element, "url", "video_url", "videoUrl", "download_url", "downloadUrl", "file_url", "fileUrl");
        if (!string.IsNullOrWhiteSpace(url))
            return await NormalizeVideoOutputAsync(url, mediaType, cancellationToken);

        var base64 = GetString(element, "b64_json", "base64", "data");
        if (!string.IsNullOrWhiteSpace(base64))
            return await NormalizeVideoOutputAsync(base64, mediaType, cancellationToken);

        if (TryGetPropertyIgnoreCase(element, "video", out var nestedVideo))
            return await NormalizeVideoOutputAsync(nestedVideo, cancellationToken);

        return null;
    }

    private async Task<VideoResponseFile?> NormalizeVideoOutputAsync(string? value, string? mediaType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (LooksLikeUrl(value))
            return await DownloadVideoAsync(value, mediaType, cancellationToken);

        if (LooksLikeDataUrl(value))
        {
            return new VideoResponseFile
            {
                Data = value.RemoveDataUrlPrefix(),
                MediaType = mediaType ?? TryGetDataUrlMediaType(value) ?? "video/mp4"
            };
        }

        return new VideoResponseFile
        {
            Data = value,
            MediaType = mediaType ?? "video/mp4"
        };
    }
}
