using AIHappey.Vercel.Models;
using OpenAI.Videos;
using System.ClientModel;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider 
{
    private static MultipartFormDataContent BuildVideoMultipart(VideoRequest request)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(request.Model), "model" },
            { new StringContent(request.Prompt), "prompt" },
            { new StringContent((request.Duration ?? 4).ToString()), "seconds" },
            { new StringContent(request.Resolution ?? "720x1280"), "size" }
        };

        if (request.Image is not null)
        {
            var bytes = Convert.FromBase64String(request.Image.Data);
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(request.Image.MediaType);

            form.Add(file, "input_reference", "reference");
        }

        return form;
    }


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

        using var multipart = BuildVideoMultipart(request);

        // must include boundary
        var contentType = multipart.Headers.ContentType?.ToString()
            ?? throw new InvalidOperationException("Multipart content-type missing boundary.");

        await using var ms = new MemoryStream();
        await multipart.CopyToAsync(ms, cancellationToken);

        // ✅ wrap bytes in BinaryData
        var binaryData = BinaryData.FromBytes(ms.ToArray());

        // ✅ THIS is the correct overload
        var content = BinaryContent.Create(binaryData);
        var result = await responseClient.CreateVideoAsync(content, contentType);

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

        using var readStream = new MemoryStream();
        await stream!.CopyToAsync(readStream, cancellationToken);
        var videoBytes = readStream.ToArray();

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
