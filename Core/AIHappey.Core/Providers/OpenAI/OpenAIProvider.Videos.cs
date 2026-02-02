using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using OpenAI.Videos;
using System.ClientModel;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        var responseClient = new VideoClient(GetKey());
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "fps"
            });
        }

        if (request.N is not null && request.N > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n"
            });
        }

        var payload = new
        {
            model = request.Model,
            prompt = request.Prompt,
            seconds = request.Duration is not null ? request.Duration?.ToString() : 4.ToString(),
            size = request.Resolution ?? "720x1280"
        };

        var json = JsonSerializer.Serialize(payload);
        var content = BinaryContent.CreateJson(json);
        var result = await responseClient.CreateVideoAsync(content, "application/json");
        using var doc = JsonDocument.Parse(result.GetRawResponse().Content.ToString());
        var videoId = doc.RootElement.GetProperty("id").GetString();

        JsonElement job;
        while (true)
        {
            var jobResult = await responseClient.GetVideoAsync(videoId!);

            using var jobDoc = JsonDocument.Parse(jobResult.GetRawResponse().Content.ToString());
            job = jobDoc.RootElement;

            var status = job.GetProperty("status").GetString();
            var progress = job.TryGetProperty("progress", out var p) ? p.GetInt32() : 0;

            if (status is "completed")
                break;

            if (status is "failed" or "error")
            {
                var error = job.TryGetProperty("error", out var e) ? e.ToString() : "Unknown error";
                throw new Exception($"Video generation failed: {error}");
            }

            // small backoff
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var downloadResult = await responseClient.DownloadVideoAsync(videoId);

        var response = downloadResult.GetRawResponse();
        await using var stream = response.ContentStream;

        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms, cancellationToken);

        var videoBytes = ms.ToArray();

        return new VideoResponse()
        {
            Videos = [new VideoResponseFile() {
                MediaType = "video/mp4",
                Data = Convert.ToBase64String(videoBytes)
            }],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }
}
