using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PixCode;

public partial class PixCodeProvider
{
    private static readonly JsonSerializerOptions PixCodeVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<VideoResponse> VideoRequestPixCode(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var providerOptions = GetPixCodeVideoProviderOptions(request);
        ValidatePixCodeVideoRequest(request, providerOptions);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        var payload = BuildPixCodeVideoPayload(request, providerOptions);
        var json = JsonSerializer.Serialize(payload, PixCodeVideoJsonOptions);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v1/video/generations")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);

        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PixCode video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var taskId = TryGetString(createDoc.RootElement, "task_id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("PixCode video generation returned no task_id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/video/generations/{taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);

                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"PixCode video status failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            root => IsTerminalStatus(TryGetString(root, "status")),
            interval: TimeSpan.FromSeconds(10),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetString(completed, "status");
        if (!IsSuccessStatus(finalStatus))
            throw new InvalidOperationException($"PixCode video generation failed with status '{finalStatus ?? "unknown"}' (task_id={taskId}). Response: {completed.GetRawText()}");

        var videoUrl = TryGetVideoUrl(completed);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException($"PixCode video task completed but returned no video url (task_id={taskId}).");

        var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
        var mediaType = ResolveVideoMediaType(completed, videoUrl) ?? "video/mp4";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                family = "video-task",
                create = createDoc.RootElement.Clone(),
                poll = completed
            }, JsonSerializerOptions.Web)
        };

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
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed
            }
        };
    }

    private Dictionary<string, object?> BuildPixCodeVideoPayload(VideoRequest request, JsonElement? providerOptions)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model
        };

        var input = new Dictionary<string, object?>();
        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            input["prompt"] = request.Prompt;

        if (request.Image is not null && !string.IsNullOrWhiteSpace(request.Image.Data))
            input["img_url"] = request.Image.Data;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["size"] = request.Resolution;

        if (request.Duration is not null)
            parameters["duration"] = request.Duration;

        if (request.Seed is not null)
            parameters["seed"] = request.Seed;

        if (providerOptions.HasValue && providerOptions.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.Value.EnumerateObject())
            {
                if (property.NameEquals("model"))
                    continue;

                if (property.NameEquals("input") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    MergeObjectValues(input, property.Value);
                    continue;
                }

                if (property.NameEquals("parameters") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    MergeObjectValues(parameters, property.Value);
                    continue;
                }

                payload[property.Name] = property.Value.Clone();
            }
        }

        if (input.Count > 0)
            payload["input"] = input;

        if (parameters.Count > 0)
            payload["parameters"] = parameters;

        return payload;
    }

    private static void MergeObjectValues(Dictionary<string, object?> target, JsonElement source)
    {
        foreach (var property in source.EnumerateObject())
            target[property.Name] = property.Value.Clone();
    }

    private static JsonElement? GetPixCodeVideoProviderOptions(VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue(nameof(PixCode).ToLowerInvariant(), out var options))
            return null;

        return options.ValueKind == JsonValueKind.Object
            ? options.Clone()
            : null;
    }

    private static void ValidatePixCodeVideoRequest(VideoRequest request, JsonElement? providerOptions)
    {
        var hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt) || HasProviderInputValue(providerOptions, "prompt");
        var hasImage = request.Image is not null || HasProviderInputValue(providerOptions, "img_url");
        var hasReferenceUrls = HasProviderInputValue(providerOptions, "reference_urls");

        if (request.Model.Contains("-t2v", StringComparison.OrdinalIgnoreCase) && !hasPrompt)
            throw new ArgumentException("Prompt is required for PixCode text-to-video models.", nameof(request));

        if (request.Model.Contains("-i2v", StringComparison.OrdinalIgnoreCase) && !hasImage)
            throw new ArgumentException("Image is required for PixCode image-to-video models.", nameof(request));

        if (request.Model.Contains("-v2v", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasPrompt)
                throw new ArgumentException("Prompt is required for PixCode reference-video models.", nameof(request));

            if (!hasReferenceUrls)
                throw new ArgumentException("reference_urls is required for PixCode reference-video models.", nameof(request));
        }

        if (!hasPrompt && !hasImage && !hasReferenceUrls)
            throw new ArgumentException("Prompt, image, or provider video input is required.", nameof(request));
    }

    private static bool HasProviderInputValue(JsonElement? providerOptions, string propertyName)
    {
        if (!providerOptions.HasValue || providerOptions.Value.ValueKind != JsonValueKind.Object)
            return false;

        if (!providerOptions.Value.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return false;

        if (!input.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            _ => true
        };
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (TryGetString(root, "video_url") is { } directUrl && !string.IsNullOrWhiteSpace(directUrl))
            return directUrl;

        if (root.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            foreach (var video in videos.EnumerateArray())
            {
                if (TryGetString(video, "video_url") is { } nestedUrl && !string.IsNullOrWhiteSpace(nestedUrl))
                    return nestedUrl;

                if (TryGetString(video, "url") is { } alternateUrl && !string.IsNullOrWhiteSpace(alternateUrl))
                    return alternateUrl;
            }
        }

        return null;
    }

    private static string? ResolveVideoMediaType(JsonElement root, string? videoUrl)
    {
        if (root.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Array)
        {
            foreach (var video in videos.EnumerateArray())
            {
                if (TryGetString(video, "video_type") is { } videoType && !string.IsNullOrWhiteSpace(videoType))
                    return NormalizeVideoMediaType(videoType);
            }
        }

        return NormalizeVideoMediaType(videoUrl);
    }

    private static string? NormalizeVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return value;

        if (value.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || value.Equals("webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (value.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        if (value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || value.Equals("mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static bool IsTerminalStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("SUCCEED", StringComparison.OrdinalIgnoreCase)
               || status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
               || status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)
               || status.Equals("ERROR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSuccessStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("SUCCEED", StringComparison.OrdinalIgnoreCase)
               || status.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase)
               || status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase);
    }
}
