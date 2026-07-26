using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var payload = CreateAIgatewayPayload(new()
        {
            ["model"] = request.Model, ["prompt"] = request.Prompt, ["duration"] = request.Duration,
            ["resolution"] = request.Resolution, ["aspect_ratio"] = request.AspectRatio,
            ["seed"] = request.Seed, ["n"] = request.N
        }, request.ProviderOptions, "model", "prompt", "duration", "resolution", "aspect_ratio", "seed", "n", "webhook_url");
        using var createRequest = CreateAIgatewayJsonRequest(HttpMethod.Post, "v1/videos/generations", payload);
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var created = await ReadAIgatewayJsonAsync(createResponse, "video generation", cancellationToken);
        var jobId = GetAIgatewayString(created, "id");
        if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException("AIgateway video generation did not return a job id.");

        var completed = await PollAIgatewayJobAsync(jobId, cancellationToken);
        if (!string.Equals(completed.Status, "completed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AIgateway video job '{jobId}' terminated with status '{completed.Status ?? "unknown"}'.");
        var fileUrl = GetAIgatewayString(completed.Root, "result", "file_url");
        if (string.IsNullOrWhiteSpace(fileUrl)) throw new InvalidOperationException("AIgateway completed video job did not include result.file_url.");
        var (video, mediaType) = await DownloadAIgatewayFileAsync(fileUrl, ResolveAIgatewayVideoMimeType(fileUrl), cancellationToken);

        return new VideoResponse
        {
            Videos = [new VideoResponseFile { Data = Convert.ToBase64String(video), MediaType = mediaType }],
            Warnings = GetVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { create = created, job = completed.Root }),
            Response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = completed.Headers, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    private static IEnumerable<object> GetVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "image inputs" });
        return warnings;
    }

    private static string ResolveAIgatewayVideoMimeType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";
}
