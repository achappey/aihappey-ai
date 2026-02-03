using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.BytePlus;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BytePlus;

public partial class BytePlusProvider : IModelProvider
{
    private static readonly JsonSerializerOptions BytePlusVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Image is not null && request.Image.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BytePlus video generation only supports base64 or data URLs for images.");

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        var metadata = GetVideoProviderMetadata<BytePlusVideoProviderMetadata>(request, GetIdentifier());
        ValidateImageRoles(metadata?.ImageRoles);

        var payload = BuildVideoPayload(request, metadata, warnings);
        var json = JsonSerializer.Serialize(payload, BytePlusVideoJson);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, "v3/contents/generations/tasks")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"BytePlus video create failed ({(int)createResp.StatusCode})"
                : $"BytePlus video create failed ({(int)createResp.StatusCode}): {createRaw}");
        }

        using var createDoc = JsonDocument.Parse(createRaw);
        var root = createDoc.RootElement;
        var taskId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("BytePlus video generation returned no id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v3/contents/generations/tasks/{taskId}");
                using var pollResp = await _client.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"BytePlus video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            result =>
            {
                var status = TryGetStatus(result);
                return string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetStatus(completed);
        if (!string.Equals(finalStatus, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            var error = completed.TryGetProperty("error", out var errEl) ? errEl.ToString() : "Unknown error";
            throw new InvalidOperationException($"BytePlus video generation failed: {error}");
        }

        var videoUrl = TryGetVideoUrl(completed);
        if (string.IsNullOrWhiteSpace(videoUrl))
            throw new InvalidOperationException("BytePlus video result contained no video_url.");

        var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
        var mediaType = GuessVideoMediaType(videoUrl) ?? "video/mp4";

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["byteplus"] = completed.Clone()
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
                Body = createRaw
            }
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(
        VideoRequest request,
        BytePlusVideoProviderMetadata? metadata,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = NormalizeVideoModelName(request.Model)
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
        {
            payload["content"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["type"] = "text",
                    ["text"] = request.Prompt
                }
            };
        }

        var content = payload.TryGetValue("content", out var contentObj) && contentObj is List<Dictionary<string, object?>> list
            ? list
            : new List<Dictionary<string, object?>>();

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

        AppendImageRoles(content, metadata?.ImageRoles);

        if (content.Count > 0)
            payload["content"] = content;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["ratio"] = request.AspectRatio;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.Seed is not null)
            payload["seed"] = request.Seed;

        if (metadata?.GenerateAudio is not null)
            payload["generate_audio"] = metadata.GenerateAudio;

        if (metadata?.Watermark is not null)
            payload["watermark"] = metadata.Watermark;

        if (metadata?.CameraFixed is not null)
            payload["camera_fixed"] = metadata.CameraFixed;

        return payload;
    }

    private static void AppendImageRoles(List<Dictionary<string, object?>> content, BytePlusVideoImageRoles? roles)
    {
        if (roles is null)
            return;

        if (!string.IsNullOrWhiteSpace(roles.FirstFrame))
        {
            content.Add(BuildImageRole(roles.FirstFrame, "first_frame"));
        }

        if (!string.IsNullOrWhiteSpace(roles.LastFrame))
        {
            content.Add(BuildImageRole(roles.LastFrame, "last_frame"));
        }

        if (roles.ReferenceImages is not null)
        {
            foreach (var image in roles.ReferenceImages)
            {
                if (string.IsNullOrWhiteSpace(image))
                    continue;

                content.Add(BuildImageRole(image, "reference_image"));
            }
        }
    }

    private static Dictionary<string, object?> BuildImageRole(string imageData, string role)
    {
        var imageUrl = imageData.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? imageData
            : imageData;

        return new Dictionary<string, object?>
        {
            ["type"] = "image_url",
            ["image_url"] = new Dictionary<string, object?>
            {
                ["url"] = imageUrl
            },
            ["role"] = role
        };
    }

    private static void ValidateImageRoles(BytePlusVideoImageRoles? roles)
    {
        if (roles is null)
            return;

        if (!string.IsNullOrWhiteSpace(roles.FirstFrame) && roles.FirstFrame.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BytePlus video generation only supports base64 or data URLs for images.");

        if (!string.IsNullOrWhiteSpace(roles.LastFrame) && roles.LastFrame.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("BytePlus video generation only supports base64 or data URLs for images.");

        if (roles.ReferenceImages is null)
            return;

        foreach (var image in roles.ReferenceImages)
        {
            if (string.IsNullOrWhiteSpace(image))
                continue;

            if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BytePlus video generation only supports base64 or data URLs for images.");
        }
    }

    private static string? TryGetStatus(JsonElement root)
    {
        return root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;
    }

    private static string? TryGetVideoUrl(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
            return null;

        if (!content.TryGetProperty("video_url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return null;

        return urlEl.GetString();
    }

    private static string NormalizeVideoModelName(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return model;

        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
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
