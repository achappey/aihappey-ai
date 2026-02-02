using AIHappey.Common.Model.Providers.Together;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Together;

public partial class TogetherProvider : IModelProvider
{
    private static readonly JsonSerializerOptions VideoJsonSettings = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

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

        if (request.Image is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "image"
            });
        }

        var metadata = GetVideoProviderMetadata<TogetherVideoProviderMetadata>(request, GetIdentifier());
        var (width, height) = ParseResolution(request.Resolution);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["seconds"] = request.Duration?.ToString(),
            ["width"] = width,
            ["height"] = height,
            ["steps"] = metadata?.Steps,
            ["guidance_scale"] = metadata?.GuidanceScale,
            ["output_format"] = metadata?.OutputFormat,
            ["output_quality"] = metadata?.OutputQuality,
            ["negative_prompt"] = metadata?.NegativePrompt
        };

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        var jsonBody = JsonSerializer.Serialize(payload, VideoJsonSettings);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v2/videos")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together video request failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var jobId = createDoc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("Together video request did not return an id.");

        var completedJob = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v2/videos/{jobId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Together video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            job =>
            {
                var status = job.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = completedJob.TryGetProperty("status", out var finalStatusEl)
            ? finalStatusEl.GetString()
            : null;

        if (!string.Equals(finalStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            var error = completedJob.TryGetProperty("error", out var errEl) ? errEl.ToString() : "Unknown error";
            throw new InvalidOperationException($"Together video generation failed: {error}");
        }

        var outputs = completedJob.TryGetProperty("outputs", out var outputsEl) ? outputsEl : default;
        var videoUrl = outputs.ValueKind == JsonValueKind.Object
            && outputs.TryGetProperty("video_url", out var urlEl)
            && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("Together video result contained no video_url.");

        var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
        var mediaType = ResolveVideoMediaType(metadata?.OutputFormat, videoUrl);

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static (int? width, int? height) ParseResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
            return (null, null);

        var normalized = resolution.Replace(":", "x", StringComparison.OrdinalIgnoreCase);
        var parts = normalized.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return (null, null);

        var width = int.TryParse(parts[0], out var w) ? w : (int?)null;
        var height = int.TryParse(parts[1], out var h) ? h : (int?)null;
        return (width, height);
    }

    private static string ResolveVideoMediaType(string? outputFormat, string? videoUrl)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
        {
            var fmt = outputFormat.Trim().ToLowerInvariant();
            if (fmt == "mp4")
                return "video/mp4";
            if (fmt == "webm")
                return "video/webm";
        }

        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            if (videoUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
                return "video/webm";
            if (videoUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return "video/mp4";
        }

        return "video/mp4";
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
