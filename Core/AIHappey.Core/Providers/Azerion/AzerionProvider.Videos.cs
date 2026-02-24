using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azerion;

public partial class AzerionProvider
{
    private const string ProviderId = "azerion";
    private const string VideoGenerationEndpoint = "v1/videos/generation";
    private const string SeedanceCreateEndpoint = "v1/contents/generations/tasks";
    private const string SeedanceTaskEndpoint = "v1/contents/generations/tasks/";

    private static readonly JsonSerializerOptions AzerionVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record SeedanceTaskResult(string? Id, string? Status, JsonElement Root);

    public async Task<VideoResponse> VideoRequestAzerion(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image/video is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var route = ResolveVideoRoute(request.Model);

        return route == VideoRoute.Seedance
            ? await RequestSeedanceVideoAsync(request, now, warnings, cancellationToken)
            : await RequestGenerationVideoAsync(request, now, warnings, cancellationToken);
    }

    private async Task<VideoResponse> RequestGenerationVideoAsync(
        VideoRequest request,
        DateTime now,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.N is not null && request.N > 4)
            warnings.Add(new { type = "unsupported", feature = "n", details = "Azerion videos/generation supports up to 4 outputs." });

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azerion video generation only supports base64 or data URLs for image/video inputs.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Trim(),
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt,
            ["n"] = request.N,
            ["duration"] = request.Duration,
            ["resolution"] = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["seed"] = request.Seed,
            ["output_format"] = "base64"
        };

        if (request.Image is not null)
            payload["image"] = BuildGenerationMediaInput(request.Image, warnings);

        MergeProviderOptions(payload, request, ProviderId);

        if (payload.TryGetValue("output_format", out var outputFormat)
            && outputFormat is string outputFormatString
            && !string.Equals(outputFormatString, "base64", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "output_format", details = "Only base64 output is supported in this integration." });
            payload["output_format"] = "base64";
        }

        var json = JsonSerializer.Serialize(payload, AzerionVideoJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, VideoGenerationEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Azerion video generation failed ({(int)resp.StatusCode})"
                : $"Azerion video generation failed ({(int)resp.StatusCode}): {raw}");
        }

        var videos = ExtractBase64Videos(raw);
        if (videos.Count == 0)
            throw new InvalidOperationException("Azerion video generation response contained no videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonDocument.Parse(raw).RootElement.Clone()
            }
        };
    }

    private async Task<VideoResponse> RequestSeedanceVideoAsync(
        VideoRequest request,
        DateTime now,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azerion Seedance only supports base64 or data URLs for image inputs.");

        var payload = BuildSeedancePayload(request, warnings);
        var json = JsonSerializer.Serialize(payload, AzerionVideoJson);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, SeedanceCreateEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"Azerion Seedance create failed ({(int)createResp.StatusCode})"
                : $"Azerion Seedance create failed ({(int)createResp.StatusCode}): {createRaw}");
        }

        var createTask = ParseSeedanceTask(createRaw);
        if (string.IsNullOrWhiteSpace(createTask.Id))
            throw new InvalidOperationException("Azerion Seedance response contained no task id.");

        var finalTask = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollSeedanceTaskAsync(createTask.Id!, ct),
            isTerminal: result => IsSeedanceTerminalStatus(result.Status),
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (!string.Equals(finalTask.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finalTask.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Azerion Seedance task failed with status '{finalTask.Status}'.");
        }

        var videos = await ExtractSeedanceVideosAsync(finalTask.Root, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException("Azerion Seedance task completed but returned no videos.");

        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["create"] = createTask.Root.Clone(),
                    ["result"] = finalTask.Root.Clone()
                }, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            // best-effort only
        }

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = finalTask.Root.Clone()
            }
        };
    }

    private static object BuildGenerationMediaInput(VideoFile file, List<object> warnings)
    {
        var mediaType = file.MediaType ?? string.Empty;
        var value = file.Data ?? string.Empty;

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Azerion video generation only supports base64 or data URLs for image/video inputs.");

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var imageData = value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? value
                : value.ToDataUrl(mediaType);

            return new Dictionary<string, object?>
            {
                ["mime_type"] = string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Image.Jpeg : mediaType,
                ["url"] = imageData
            };
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            var videoData = value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? ExtractBase64FromDataUrl(value)
                : value;

            warnings.Add(new { type = "unsupported", feature = "video_extension", details = "Video extension uses base64 input only." });

            return new Dictionary<string, object?>
            {
                ["mime_type"] = string.IsNullOrWhiteSpace(mediaType) ? "video/mp4" : mediaType,
                ["base_64_encoded"] = videoData
            };
        }

        throw new ArgumentException($"Unsupported mediaType '{file.MediaType}'. Expected image/* or video/*.", nameof(file));
    }

    private static Dictionary<string, object?> BuildSeedancePayload(VideoRequest request, List<object> warnings)
    {
        var content = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = request.Prompt
            });
        }

        if (request.Image is not null)
        {
            var imageData = request.Image.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? request.Image.Data
                : request.Image.Data.ToDataUrl(request.Image.MediaType);

            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = imageData
                }
            });
        }

        if (content.Count == 0)
            throw new ArgumentException("Prompt or image is required for Seedance models.", nameof(request));

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Trim(),
            ["content"] = content
        };

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["ratio"] = request.AspectRatio;

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        MergeProviderOptions(payload, request, ProviderId);

        return payload;
    }

    private async Task<SeedanceTaskResult> PollSeedanceTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, SeedanceTaskEndpoint + Uri.EscapeDataString(taskId));
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Azerion Seedance poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        return ParseSeedanceTask(pollRaw);
    }

    private static SeedanceTaskResult ParseSeedanceTask(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        return new SeedanceTaskResult(id, status, root);
    }

    private static bool IsSeedanceTerminalStatus(string? status)
        => string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);

    private async Task<List<VideoResponseFile>> ExtractSeedanceVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (root.TryGetProperty("videos", out var videosEl) && videosEl.ValueKind == JsonValueKind.Array)
        {
            return await ExtractVideoArrayAsync(videosEl, cancellationToken);
        }

        if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Object)
        {
            if (contentEl.TryGetProperty("videos", out var contentVideos) && contentVideos.ValueKind == JsonValueKind.Array)
                return await ExtractVideoArrayAsync(contentVideos, cancellationToken);
        }

        return [];
    }

    private async Task<List<VideoResponseFile>> ExtractVideoArrayAsync(JsonElement videosEl, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        foreach (var item in videosEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("base64_encoded", out var base64El) && base64El.ValueKind == JsonValueKind.String)
                {
                    var b64 = base64El.GetString();
                    if (!string.IsNullOrWhiteSpace(b64))
                    {
                        videos.Add(new VideoResponseFile
                        {
                            MediaType = "video/mp4",
                            Data = b64
                        });
                    }

                    continue;
                }

                if (item.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    var url = urlEl.GetString();
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
                    var mediaType = GuessVideoMediaType(url) ?? "video/mp4";
                    videos.Add(new VideoResponseFile
                    {
                        MediaType = mediaType,
                        Data = Convert.ToBase64String(bytes)
                    });

                    continue;
                }
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = await _client.GetByteArrayAsync(value, cancellationToken);
                    var mediaType = GuessVideoMediaType(value) ?? "video/mp4";
                    videos.Add(new VideoResponseFile
                    {
                        MediaType = mediaType,
                        Data = Convert.ToBase64String(bytes)
                    });

                    continue;
                }

                videos.Add(new VideoResponseFile
                {
                    MediaType = "video/mp4",
                    Data = value
                });
            }
        }

        return videos;
    }

    private static List<VideoResponseFile> ExtractBase64Videos(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("videos", out var videosEl) || videosEl.ValueKind != JsonValueKind.Array)
            return [];

        List<VideoResponseFile> videos = [];
        foreach (var item in videosEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (item.TryGetProperty("base64_encoded", out var base64El) && base64El.ValueKind == JsonValueKind.String)
            {
                var b64 = base64El.GetString();
                if (string.IsNullOrWhiteSpace(b64))
                    continue;

                videos.Add(new VideoResponseFile
                {
                    MediaType = "video/mp4",
                    Data = b64
                });
            }
        }

        return videos;
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static string ExtractBase64FromDataUrl(string dataUrl)
    {
        var index = dataUrl.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            throw new InvalidOperationException("Input data URL missing base64 content.");

        return dataUrl[(index + "base64,".Length)..];
    }

    private static VideoRoute ResolveVideoRoute(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return VideoRoute.Generation;

        return model.Contains("seedance", StringComparison.OrdinalIgnoreCase)
            ? VideoRoute.Seedance
            : VideoRoute.Generation;
    }

    private static void MergeProviderOptions(Dictionary<string, object?> payload, VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue(providerId, out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (payload.ContainsKey(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private enum VideoRoute
    {
        Generation,
        Seedance
    }
}
