using System.Net.Mime;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PiAPI;

public partial class PiAPIProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "PiAPI task outputs are provider-defined." });

        var input = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio,
            ["seed"] = request.Seed
        };

        var imageUrls = new List<string>();
        var videoUrls = new List<string>();
        var audioUrls = new List<string>();
        AddVideoFile(request.Image, imageUrls, videoUrls, audioUrls);
        foreach (var reference in request.InputReferences ?? [])
            AddVideoFile(reference, imageUrls, videoUrls, audioUrls);
        foreach (var frame in request.FrameImages ?? [])
            AddVideoFile(frame.Image, imageUrls, videoUrls, audioUrls);

        if (imageUrls.Count > 0)
            input["image_urls"] = imageUrls;
        if (videoUrls.Count > 0)
            input["video_urls"] = videoUrls;
        if (audioUrls.Count > 0)
            input["audio_urls"] = audioUrls;
        if (request.FrameImages?.Any() == true && imageUrls.Count is > 0 and <= 2)
            input["mode"] = "first_last_frames";
        else if (imageUrls.Count > 0 || videoUrls.Count > 0 || audioUrls.Count > 0)
            input["mode"] = "omni_reference";
        else
            input["mode"] = "text_to_video";

        var task = await CreateAndWaitForMediaTaskAsync(request.Model, "txt2video", input, request.ProviderOptions, cancellationToken);
        var videos = new List<VideoResponseFile>();
        foreach (var output in GetOutputValues(task.Result.Root, "video", "video_url", "video_urls", "videos"))
        {
            var video = await DownloadMediaAsync(output, "video/mp4", cancellationToken);
            videos.Add(new VideoResponseFile { Data = video.Base64, MediaType = video.MimeType });
        }

        if (videos.Count == 0)
            throw new InvalidOperationException("PiAPI video task completed without generated video.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = CreateMediaProviderMetadata(task.Create, task.Result),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static void AddVideoFile(VideoFile? file, List<string> imageUrls, List<string> videoUrls, List<string> audioUrls)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Data))
            return;

        var value = file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? file.Data
                : ToDataUrl(file.Data, string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType);

        if (file.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            videoUrls.Add(value);
        else if (file.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            audioUrls.Add(value);
        else
            imageUrls.Add(value);
    }
}
