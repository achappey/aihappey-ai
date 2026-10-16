using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Getimg;

public partial class GetimgProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var model = NormalizeGetimgModel(request.Model);
        var warnings = new List<object>();
        var payload = CreateGetimgPayload(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = model;
        payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (request.Duration is not null) payload["duration"] = request.Duration;
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generateAudio" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });

        var images = new List<object>();
        if (request.Image is not null)
            images.Add(new { url = ToGetimgInputUrl(request.Image), role = "first_frame" });
        foreach (var frame in request.FrameImages ?? [])
            if (frame?.Image is not null)
                images.Add(new { url = ToGetimgInputUrl(frame.Image), role = frame.FrameType });
        foreach (var reference in request.InputReferences ?? [])
            if (reference is not null)
                images.Add(new { url = ToGetimgInputUrl(reference), role = "reference_image" });
        if (images.Count > 0 && payload.ContainsKey("images"))
            warnings.Add(new { type = "other", message = "providerOptions.getimg.images overrides request images." });
        else if (images.Count > 0)
            payload["images"] = images;

        var root = await SendGetimgAsync(HttpMethod.Post, "videos/generations", payload, cancellationToken);
        var id = GetGetimgString(root, "id")
            ?? throw new InvalidOperationException("getimg video generation response did not contain an id.");

        return new VideoOperationStartResult
        {
            Operation = id,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));

        var root = await SendGetimgAsync(HttpMethod.Get, $"videos/generations/{Uri.EscapeDataString(operation)}", null, cancellationToken);
        var status = GetGetimgString(root, "status") ?? "pending";
        var model = GetGetimgString(root, "model");
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root, GetGetimgCost(root));

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = GetGetimgError(root), ProviderMetadata = metadata, Response = response };
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var videos = new List<VideoOperationVideoData>();
        foreach (var output in GetGetimgOutputs(root))
        {
            var url = GetGetimgString(output, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            var (bytes, mediaType) = await DownloadGetimgOutputAsync(url, GetGetimgString(output, "mime_type"), "video/mp4", cancellationToken);
            videos.Add(new VideoOperationVideoData { Type = "base64", MediaType = mediaType, Data = Convert.ToBase64String(bytes) });
        }

        if (videos.Count == 0)
            return new VideoOperationErrorResult { Error = "getimg video generation completed without a video URL.", ProviderMetadata = metadata, Response = response };

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }
}
