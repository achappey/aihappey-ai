using System.Net.Mime;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Glio;

public partial class GlioProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var options = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyGlioRootOptions(options);
        var parameters = GetGlioParams(payload);

        payload["model"] = request.Model;
        payload["action"] = "generate";
        parameters["prompt"] = request.Prompt;
        SetGlioValue(parameters, "resolution", request.Resolution);
        SetGlioValue(parameters, "aspect_ratio", request.AspectRatio);
        SetGlioValue(parameters, "seed", request.Seed);
        SetGlioValue(parameters, "duration", request.Duration);
        SetGlioValue(parameters, "fps", request.Fps);
        SetGlioValue(parameters, "n", request.N);

        if (request.Image is not null)
            parameters["image"] = ToGlioDataUrl(request.Image.Data, request.Image.MediaType);

        var references = request.InputReferences?
            .Where(reference => reference is not null)
            .Select(reference => ToGlioDataUrl(reference.Data, reference.MediaType))
            .ToList() ?? [];
        if (references.Count > 0)
            parameters["input_references"] = references;

        var frames = request.FrameImages?
            .Where(frame => frame?.Image is not null)
            .Select(frame => new Dictionary<string, object?>
            {
                ["frame_type"] = frame.FrameType,
                ["image"] = ToGlioDataUrl(frame.Image.Data, frame.Image.MediaType)
            })
            .ToList() ?? [];
        if (frames.Count > 0)
            parameters["frame_images"] = frames;

        var job = await RunGlioJobAsync(payload, cancellationToken);
        var videos = new List<VideoResponseFile>(job.Urls.Count);
        foreach (var url in job.Urls)
        {
            var media = await DownloadGlioMediaAsync(url, GuessGlioVideoMediaType(url), cancellationToken);
            videos.Add(new VideoResponseFile
            {
                Type = "base64",
                Data = Convert.ToBase64String(media.Bytes),
                MediaType = media.MediaType
            });
        }

        var deletion = await DeleteGlioJobAsync(job.JobId, cancellationToken);
        job = job with { Delete = deletion };

        return new VideoResponse
        {
            Videos = videos,
            ProviderMetadata = CreateGlioJobMetadata(job),
            Response = new()
            {
                Timestamp = now,
                Headers = job.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string GuessGlioVideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
    }
}
