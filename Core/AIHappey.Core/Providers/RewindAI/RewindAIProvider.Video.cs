using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{
   public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        List<object> warnings = [];
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "image", message = "RewindAI's documented video endpoint supports text-to-video only." });

        var payload = CreateRewindAIPayload(request.ProviderOptions,
            ("model", request.Model),
            ("prompt", request.Prompt),
            ("duration", request.Duration is null ? null : $"{request.Duration}s"),
            ("aspectRatio", request.AspectRatio),
            ("resolution", request.Resolution),
            ("seed", request.Seed),
            ("n", request.N));
        var requestBody = JsonSerializer.Serialize(payload, RewindAIJson);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generate-async")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI video submission failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var jobId = ReadRewindAIString(createRoot, "id", "jobId", "job_id");
        if (string.IsNullOrWhiteSpace(jobId) && createRoot.TryGetProperty("job", out var job))
            jobId = ReadRewindAIString(job, "id", "jobId", "job_id");
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("RewindAI video submission response did not contain a job id.");

        List<JsonElement> jobResponses = [createRoot];
        var completed = await PollRewindAIVideoAsync(jobId, jobResponses, cancellationToken);
        if (IsRewindAIVideoFailure(completed))
            throw new InvalidOperationException($"RewindAI video generation failed for job '{jobId}': {completed.GetRawText()}");

        var videos = await ExtractRewindAIVideosAsync(completed, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException($"RewindAI video job '{jobId}' completed without video output.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                jobId,
                responses = jobResponses
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = ReadRewindAIString(completed, "model").ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<JsonElement> PollRewindAIVideoAsync(
        string jobId,
        List<JsonElement> responses,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException($"RewindAI video job '{jobId}' did not complete within 10 minutes.");

            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
            using var response = await _client.SendAsync(request, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"RewindAI video job poll failed ({(int)response.StatusCode}): {raw}");

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement.Clone();
            responses.Add(root);
            if (IsRewindAIVideoTerminal(root))
                return root;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static bool IsRewindAIVideoTerminal(JsonElement job)
    {
        var status = ReadRewindAIString(job, "status", "state");
        if (string.IsNullOrWhiteSpace(status) && job.TryGetProperty("job", out var nestedJob))
            status = ReadRewindAIString(nestedJob, "status", "state");

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRewindAIVideoFailure(JsonElement job)
    {
        var status = ReadRewindAIString(job, "status", "state");
        if (string.IsNullOrWhiteSpace(status) && job.TryGetProperty("job", out var nestedJob))
            status = ReadRewindAIString(nestedJob, "status", "state");

        return status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<VideoResponseFile>> ExtractRewindAIVideosAsync(JsonElement job, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];
        CollectRewindAIVideoValues(job, videos);
        if (videos.Count > 0)
            return videos;

        var downloadUrl = ReadRewindAIString(job, "download_url", "content_url");
        if (IsRewindAIAbsoluteUrl(downloadUrl))
            videos.Add(await DownloadRewindAIVideoAsync(downloadUrl, cancellationToken));

        return videos;
    }

    private static void CollectRewindAIVideoValues(JsonElement element, List<VideoResponseFile> videos)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "video_url", "url", "video", "output_url", "output", "result", "data" })
            {
                if (!element.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.String)
                    AddRewindAIVideoValue(videos, value.GetString(), ReadRewindAIString(element, "mime_type", "media_type", "content_type"));
                else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    CollectRewindAIVideoValues(value, videos);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectRewindAIVideoValues(item, videos);
        }
    }

    private static void AddRewindAIVideoValue(List<VideoResponseFile> videos, string? value, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (IsRewindAIAbsoluteUrl(value))
            return;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(',');
            if (separator > 0)
            {
                var declaredType = value[5..separator].Split(';', 2)[0];
                videos.Add(new VideoResponseFile { Data = value[(separator + 1)..], MediaType = declaredType });
            }
            return;
        }

        try
        {
            _ = Convert.FromBase64String(value);
            videos.Add(new VideoResponseFile { Data = value, MediaType = string.IsNullOrWhiteSpace(mediaType) ? "video/mp4" : mediaType });
        }
        catch (FormatException)
        {
            // The field was not binary video content.
        }
    }

    private async Task<VideoResponseFile> DownloadRewindAIVideoAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI video download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = response.Content.Headers.ContentType?.MediaType ?? "video/mp4"
        };
    }
}
