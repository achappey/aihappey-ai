using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private async Task<VideoResponse> DeapiVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        var now = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var warnings = new List<object>();

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var (width, height) = ResolveVideoSize(request.Resolution, request.AspectRatio);
        var guidance = TryGetNumber(metadata, "guidance") ?? 3.0;
        var steps = (int)(TryGetNumber(metadata, "steps") ?? 30);
        var frames = request.Duration is > 0 ? request.Duration.Value : (int)(TryGetNumber(metadata, "frames") ?? 49);
        var seed = request.Seed ?? (int)(TryGetNumber(metadata, "seed") ?? -1);
        var fps = request.Fps ?? (int)(TryGetNumber(metadata, "fps") ?? 30);
        var webhookUrl = TryGetString(metadata, "webhook_url") ?? TryGetString(metadata, "webhookUrl");

        string endpoint;
        string requestId;
        if (request.Image is not null)
        {
            endpoint = "api/v1/client/img2video";

            var imageBytes = DecodeBase64Payload(request.Image.Data);
            using var form = new MultipartFormDataContent();

            var firstFrame = new ByteArrayContent(imageBytes);
            firstFrame.Headers.ContentType = new MediaTypeHeaderValue(request.Image.MediaType);
            form.Add(firstFrame, "first_frame_image", "first-frame" + GetImageExtension(request.Image.MediaType));

            form.Add(new StringContent(request.Prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent(width.ToString(System.Globalization.CultureInfo.InvariantCulture)), "width");
            form.Add(new StringContent(height.ToString(System.Globalization.CultureInfo.InvariantCulture)), "height");
            form.Add(new StringContent(guidance.ToString(System.Globalization.CultureInfo.InvariantCulture)), "guidance");
            form.Add(new StringContent(steps.ToString(System.Globalization.CultureInfo.InvariantCulture)), "steps");
            form.Add(new StringContent(frames.ToString(System.Globalization.CultureInfo.InvariantCulture)), "frames");
            form.Add(new StringContent(seed.ToString(System.Globalization.CultureInfo.InvariantCulture)), "seed");

            if (!string.IsNullOrWhiteSpace(webhookUrl))
                form.Add(new StringContent(webhookUrl), "webhook_url");

            requestId = await SubmitMultipartJobAsync(endpoint, form, cancellationToken);
        }
        else
        {
            endpoint = "api/v1/client/txt2video";
            using var form = new MultipartFormDataContent();

            form.Add(new StringContent(request.Prompt ?? string.Empty), "prompt");
            form.Add(new StringContent(request.Model), "model");
            form.Add(new StringContent(width.ToString(System.Globalization.CultureInfo.InvariantCulture)), "width");
            form.Add(new StringContent(height.ToString(System.Globalization.CultureInfo.InvariantCulture)), "height");
            form.Add(new StringContent(guidance.ToString(System.Globalization.CultureInfo.InvariantCulture)), "guidance");
            form.Add(new StringContent(steps.ToString(System.Globalization.CultureInfo.InvariantCulture)), "steps");
            form.Add(new StringContent(frames.ToString(System.Globalization.CultureInfo.InvariantCulture)), "frames");
            form.Add(new StringContent(seed.ToString(System.Globalization.CultureInfo.InvariantCulture)), "seed");
            form.Add(new StringContent(fps.ToString(System.Globalization.CultureInfo.InvariantCulture)), "fps");

            if (!string.IsNullOrWhiteSpace(webhookUrl))
                form.Add(new StringContent(webhookUrl), "webhook_url");

            requestId = await SubmitMultipartJobAsync(endpoint, form, cancellationToken);
        }

        var completed = await WaitForJobResultAsync(requestId, cancellationToken);
        var resultUrl = GetResultUrl(completed)
            ?? throw new InvalidOperationException($"DeAPI video result_url missing for request {requestId}.");

        var (videoBytes, mediaType) = await DownloadResultAsync(resultUrl, "video/mp4", cancellationToken);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    Data = Convert.ToBase64String(videoBytes),
                    MediaType = mediaType
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new { requestId, endpoint, resultUrl }
            }
        };
    }

    private static (int width, int height) ResolveVideoSize(string? resolution, string? aspectRatio)
    {
        if (TryParseSize(resolution, out var w, out var h))
            return (w, h);

        if (!string.IsNullOrWhiteSpace(aspectRatio))
        {
            var inferred = aspectRatio.InferSizeFromAspectRatio(minWidth: 256, maxWidth: 1536, minHeight: 256, maxHeight: 1536);
            if (inferred is not null)
                return inferred.Value;
        }

        return (512, 512);
    }
}

