using AIHappey.Common.Extensions;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.xAI;

public partial class XAIProvider : IModelProvider
{
    private static readonly JsonSerializerOptions VideoJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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

        if (request.Seed is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "seed"
            });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = request.Resolution,
            ["aspect_ratio"] = request.AspectRatio
        };

        if (request.Image is not null)
        {
            payload["image"] = request.Image.Data.ToDataUrl(request.Image.MediaType);
        }

        var json = JsonSerializer.Serialize(payload, VideoJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new Exception(string.IsNullOrWhiteSpace(raw) ? resp.ReasonPhrase : raw);

        using var doc = JsonDocument.Parse(raw);
        var requestId = doc.RootElement.TryGetProperty("request_id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(requestId))
            throw new Exception("xAI video generation returned no request_id.");

        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{requestId}");
            using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
            var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

            if (!pollResp.IsSuccessStatusCode)
                throw new Exception(string.IsNullOrWhiteSpace(pollRaw) ? pollResp.ReasonPhrase : pollRaw);

            using var pollDoc = JsonDocument.Parse(pollRaw);
            var root = pollDoc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
            if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(status))
                continue;

            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            {
                var videoUrl = TryGetVideoUrl(root);
                if (string.IsNullOrWhiteSpace(videoUrl))
                    throw new Exception("xAI video result contained no video url.");

                var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);

                return new VideoResponse
                {
                    Videos =
                    [
                        new VideoResponseFile
                        {
                            MediaType = "video/mp4",
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

            throw new Exception($"xAI video generation failed with status '{status}'.");
        }

        throw new TimeoutException("Timed out waiting for xAI video generation result.");
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("video", out var videoEl) && videoEl.ValueKind == JsonValueKind.Object)
        {
            var url = videoEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.Object)
        {
            if (responseEl.TryGetProperty("video", out var responseVideoEl)
                && responseVideoEl.ValueKind == JsonValueKind.Object)
            {
                var url = responseVideoEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                    ? urlEl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            if (responseEl.TryGetProperty("url", out var responseUrlEl) && responseUrlEl.ValueKind == JsonValueKind.String)
            {
                var url = responseUrlEl.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        return null;
    }
}
