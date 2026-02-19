using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = NormalizeRunpodModelId(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var input = BuildRunpodVideoInput(request);
        var passthroughInput = TryGetRunpodPassthroughInput(request);
        MergeJsonObject(input, passthroughInput);

        if (input.Count == 0)
            throw new ArgumentException("Video input is required (prompt, image, or providerOptions.runpod.input).", nameof(request));

        var payload = new JsonObject
        {
            ["input"] = input
        };

        var route = $"{model}/runsync";
        var payloadJson = payload.ToJsonString(JsonSerializerOptions.Web);

        using var submitResp = await _client.PostAsync(
            route,
            new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken).ConfigureAwait(false);

        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runpod video request failed ({(int)submitResp.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var root = submitDoc.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "TIMED_OUT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Runpod video generation failed with status '{status}': {root.GetRawText()}");
        }

        var videoUrls = ExtractVideoUrls(root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (videoUrls.Count == 0)
            throw new InvalidOperationException("Runpod video response did not contain any video URLs.");

        List<VideoResponseFile> videos = [];
        foreach (var videoUrl in videoUrls)
        {
            using var mediaResp = await _client.GetAsync(videoUrl, cancellationToken).ConfigureAwait(false);
            var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!mediaResp.IsSuccessStatusCode)
            {
                var text = Encoding.UTF8.GetString(bytes);
                throw new InvalidOperationException($"Runpod video download failed ({(int)mediaResp.StatusCode}): {text}");
            }

            var mediaType = mediaResp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(videoUrl)
                ?? "video/mp4";

            videos.Add(new VideoResponseFile
            {
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            });
        }

        var runpodMetadata = new Dictionary<string, JsonElement>
        {
            ["submit"] = root.Clone(),
            ["video_urls"] = JsonSerializer.SerializeToElement(videoUrls, JsonSerializerOptions.Web),
            ["resolved_input"] = JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web)
        };

        if (passthroughInput is not null)
            runpodMetadata["passthrough_input"] = JsonSerializer.SerializeToElement(passthroughInput, JsonSerializerOptions.Web);

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(runpodMetadata, JsonSerializerOptions.Web)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

    private static JsonObject BuildRunpodVideoInput(VideoRequest request)
    {
        var input = new JsonObject();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            input["prompt"] = request.Prompt;

        if (request.Duration is not null)
            input["duration"] = request.Duration.Value;

        if (request.Seed is not null)
            input["seed"] = request.Seed.Value;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            input["size"] = NormalizeRunpodVideoSize(request.Resolution!);

        if (request.Image is not null)
            input["image"] = ToRunpodVideoImageInput(request.Image);

        return input;
    }

    private static JsonObject? TryGetRunpodPassthroughInput(VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("runpod", out var runpod))
            return null;

        if (runpod.ValueKind != JsonValueKind.Object)
            return null;

        if (!runpod.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;

        return JsonNode.Parse(input.GetRawText()) as JsonObject;
    }

    private static void MergeJsonObject(JsonObject target, JsonObject? overrides)
    {
        if (overrides is null)
            return;

        foreach (var pair in overrides)
            target[pair.Key] = pair.Value?.DeepClone();
    }

    private static string NormalizeRunpodVideoSize(string value)
    {
        var normalized = value.Trim();
        return normalized.Contains('x', StringComparison.OrdinalIgnoreCase)
            ? normalized.Replace('x', '*').Replace('X', '*')
            : normalized;
    }

    private static string ToRunpodVideoImageInput(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));

        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;

        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        if (!string.IsNullOrWhiteSpace(file.MediaType))
            return $"data:{file.MediaType};base64,{file.Data}";

        return file.Data;
    }

    private static List<string> ExtractVideoUrls(JsonElement root)
    {
        List<string> urls = [];
        CollectVideoUrls(root, urls);
        return urls;
    }

    private static void CollectVideoUrls(JsonElement element, List<string> urls)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String
                        && IsVideoUrlProperty(property.Name)
                        && TryGetUrl(property.Value.GetString(), out var videoUrl)
                        && IsVideoUrlCandidate(property.Name, videoUrl))
                    {
                        urls.Add(videoUrl);
                    }

                    CollectVideoUrls(property.Value, urls);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectVideoUrls(item, urls);
                break;

            case JsonValueKind.String:
                if (TryGetUrl(element.GetString(), out var candidate) && LooksLikeVideoUrl(candidate))
                    urls.Add(candidate);
                break;
        }
    }

    private static bool IsVideoUrlProperty(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Equals("video_url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("videoUrl", StringComparison.OrdinalIgnoreCase)
            || name.Equals("video", StringComparison.OrdinalIgnoreCase)
            || name.Equals("url", StringComparison.OrdinalIgnoreCase)
            || name.Equals("uri", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVideoUrlCandidate(string propertyName, string url)
    {
        if (propertyName.Equals("video_url", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("videoUrl", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LooksLikeVideoUrl(url);
    }

    private static bool TryGetUrl(string? value, out string url)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            url = uri.ToString();
            return true;
        }

        url = string.Empty;
        return false;
    }

    private static bool LooksLikeVideoUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("video", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        if (url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return "application/vnd.apple.mpegurl";

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}

