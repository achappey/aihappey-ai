using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OneInfer;

public partial class OneInferProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = GetOneInferProviderOptions(request.ProviderOptions);
        var payload = OneInferJsonObjectToDictionary(metadata);

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Duration.HasValue)
            payload["duration"] = request.Duration.Value;
        if (request.Fps.HasValue)
            payload["fps"] = request.Fps.Value;
        if (request.Seed.HasValue)
            payload["seed"] = request.Seed.Value;
        if (request.N.HasValue)
            payload["number"] = request.N.Value;

        var references = ResolveOneInferVideoImageReferences(request).ToList();
        if (references.Count > 0)
            payload["files"] = references;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ula/generate-video")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OneInferJsonOptions),
                Encoding.UTF8,
                MediaTypeHeaderValue.Parse(MediaTypeNames.Application.Json))
        };

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneInfer video generation failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var data = OneInferGetData(root);
        var videos = await ExtractOneInferVideosAsync(data, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException("OneInfer video generation response contained no videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = ReadOneInferUnixTimestamp(data, "created") ?? now,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static IEnumerable<string> ResolveOneInferVideoImageReferences(VideoRequest request)
    {
        if (request.Image is not null)
            yield return NormalizeOneInferVideoFile(request.Image);

        if (request.InputReferences is not null)
        {
            foreach (var reference in request.InputReferences)
                if (reference is not null)
                    yield return NormalizeOneInferVideoFile(reference);
        }

        if (request.FrameImages is not null)
        {
            foreach (var frame in request.FrameImages)
                if (frame?.Image is not null)
                    yield return NormalizeOneInferVideoFile(frame.Image);
        }
    }

    private static string NormalizeOneInferVideoFile(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        var mediaType = string.IsNullOrWhiteSpace(file.MediaType)
            ? MediaTypeNames.Image.Png
            : file.MediaType;

        return file.Data.ToDataUrl(mediaType);
    }

    private async Task<List<VideoResponseFile>> ExtractOneInferVideosAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var videos = new List<VideoResponseFile>();

        if (!data.TryGetProperty("videos", out var videosElement) || videosElement.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in videosElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var type = OneInferTryGetString(item, "type", "format") ?? "mp4";
            var mediaType = OneInferVideoMediaTypeFromFormat(type);
            var base64 = OneInferTryGetString(item, "base64", "base64_data", "data", "b64_json");

            if (!string.IsNullOrWhiteSpace(base64))
            {
                videos.Add(new VideoResponseFile
                {
                    Data = base64.RemoveDataUrlPrefix(),
                    MediaType = OneInferTryGetDataUrlMediaType(base64) ?? mediaType,
                    Type = "base64"
                });
                continue;
            }

            var url = OneInferTryGetString(item, "url", "video_url", "videoUrl");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            videos.Add(await NormalizeOneInferVideoOutputAsync(url, mediaType, cancellationToken));
        }

        return videos;
    }

    private async Task<VideoResponseFile> NormalizeOneInferVideoOutputAsync(string value, string fallbackMediaType, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoResponseFile
            {
                Data = value.RemoveDataUrlPrefix(),
                MediaType = OneInferTryGetDataUrlMediaType(value) ?? fallbackMediaType,
                Type = "base64"
            };
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var videoResponse = await _client.GetAsync(uri, cancellationToken);
            var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!videoResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"Failed to download OneInfer video from returned URL ({(int)videoResponse.StatusCode}).");

            return new VideoResponseFile
            {
                Data = Convert.ToBase64String(bytes),
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType
                    ?? OneInferGuessVideoMediaType(value)
                    ?? fallbackMediaType,
                Type = "base64"
            };
        }

        return new VideoResponseFile
        {
            Data = value.RemoveDataUrlPrefix(),
            MediaType = fallbackMediaType,
            Type = "base64"
        };
    }
}
