using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Astria;

public sealed partial class AstriaProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        var model = NormalizeModel(request.Model);
        var catalog = await GetCatalogAsync(cancellationToken);
        if (!catalog.TryGetProperty("video_model_families", out var families) || families.ValueKind != JsonValueKind.Object
            || !families.TryGetProperty(model, out var family) || family.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Unknown Astria video model '{request.Model}'.");
        var defaultModel = Text(catalog, "default")
            ?? throw new InvalidOperationException("Astria catalog has no default image model for the video image stage.");
        var tune = await ResolveTuneAsync(defaultModel, cancellationToken);
        var options = Options(request.ProviderOptions);
        options["video_model"] = model;
        options["video_prompt"] = request.Prompt;
        if (request.Resolution is not null) options["video_resolution"] = request.Resolution;
        if (request.Duration.HasValue) options["video_duration"] = request.Duration.Value;
        if (request.GenerateAudio.HasValue) options["video_audio"] = request.GenerateAudio.Value;
        if (request.Seed.HasValue) options["seed"] = request.Seed.Value;
        if (request.N.HasValue) options["num_images"] = request.N.Value;
        if (request.AspectRatio is not null) options["aspect_ratio"] = request.AspectRatio;
        var first = request.Image;
        VideoFile? last = null;
        foreach (var frame in request.FrameImages ?? [])
        {
            switch (frame.FrameType)
            {
                case "first_frame":
                    if (first is not null) throw new ArgumentException("Astria accepts only one first frame.");
                    first = frame.Image;
                    break;
                case "last_frame":
                    if (last is not null) throw new ArgumentException("Astria accepts only one last frame.");
                    last = frame.Image;
                    break;
                default: throw new NotSupportedException($"Astria does not support frame type '{frame.FrameType}'.");
            }
        }
        var inputs = request.InputReferences?.ToList() ?? [];
        var videoInputs = inputs.Where(x => x.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var audioInputs = inputs.Where(x => x.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true).ToList();
        if (videoInputs.Count > 1 || audioInputs.Count > 1)
            throw new NotSupportedException("Astria accepts one reference video and one reference audio file.");
        var references = inputs.Except(videoInputs).Except(audioInputs).ToList();
        if (references.Any(IsRemoteReference) && references.Any(x => !IsRemoteReference(x)))
            throw new NotSupportedException("Astria reference order requires all URLs or all binary files, not a mixed sequence.");
        if (first is not null) RemoveMediaOptions(options, "video_first_frame");
        if (last is not null) RemoveMediaOptions(options, "video_last_frame");
        if (videoInputs.Count > 0) RemoveMediaOptions(options, "input_video");
        if (audioInputs.Count > 0) RemoveMediaOptions(options, "audio_reference");
        if (references.Count > 0)
        {
            options.Remove("image_references");
            options.Remove("image_reference_urls");
        }
        using var form = Form(options);
        if (first is not null) AddMedia(form, "video_first_frame", (first as VideoFileUrl)?.Url, first.Data, first.MediaType);
        if (last is not null) AddMedia(form, "video_last_frame", (last as VideoFileUrl)?.Url, last.Data, last.MediaType);
        foreach (var input in videoInputs)
            AddMedia(form, "input_video", (input as VideoFileUrl)?.Url, input.Data, input.MediaType);
        foreach (var input in audioInputs)
            AddMedia(form, "audio_reference", (input as VideoFileUrl)?.Url, input.Data, input.MediaType);
        foreach (var reference in references)
            if (IsRemoteReference(reference))
                form.Add(new StringContent((reference as VideoFileUrl)?.Url ?? reference.Data!), "prompt[image_reference_urls][]");
            else
                AddBinary(form, "prompt[image_references][]", reference.Data, reference.MediaType);
        var root = await SubmitAsync(tune, form, cancellationToken);
        return new VideoOperationStartResult
        {
            Operation = $"{tune}:{PromptId(root)}",
            Warnings = request.Fps.HasValue ? [new { type = "unsupported", feature = "fps" }] : [],
            ProviderMetadata = Metadata(root),
            Response = new() { ModelId = model.ToModelId(GetIdentifier()), Timestamp = DateTime.UtcNow }
        };
    }

    private static bool IsRemoteReference(VideoFile file)
        => file is VideoFileUrl || RemoteMediaUrl(file.Data) is not null;

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var parts = operation?.Split(':') ?? [];
        if (parts.Length != 2 || PositiveId(parts[0]) is not { } tune || PositiveId(parts[1]) is not { } id)
            throw new ArgumentException("Invalid Astria video operation; expected TUNE_ID:PROMPT_ID.", nameof(operation));
        var root = await PollAsync(tune, id, cancellationToken);
        var metadata = Metadata(root);
        var response = new HeaderResponseData
        {
            ModelId = (Text(root, "video_model_family") ?? Text(root, "video_model") ?? tune).ToModelId(GetIdentifier()),
            Timestamp = DateTime.UtcNow
        };
        if (!string.IsNullOrWhiteSpace(Text(root, "user_error")))
            return new VideoOperationErrorResult { Error = Text(root, "user_error")!, ProviderMetadata = metadata, Response = response };
        if (!Outputs(root).Any())
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };
        var videos = new List<VideoOperationVideoData>();
        foreach (var url in Outputs(root))
        {
            if (await DownloadVideoAsync(url, cancellationToken) is { } video)
                videos.Add(video);
        }
        if (videos.Count == 0)
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };
        return new VideoOperationCompletedResult { Videos = videos, ProviderMetadata = metadata, Response = response };
    }

    private async Task<VideoOperationVideoData?> DownloadVideoAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Astria returned an invalid media URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        // The image stage can populate images before the video is ready.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) != true)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(bytes), MediaType = mediaType };
    }
}
