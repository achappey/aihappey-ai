using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider
{
    private const decimal H3PricePerSecond768P = 0.08m;
    private const decimal H3PricePerSecond2K = 0.13m;
    private const decimal H3AdditionalImagePrice = 0.04m;
    private const int H3FreeImageCount = 5;

    private static readonly HashSet<string> H3Ratios = new(StringComparer.OrdinalIgnoreCase)
    {
        "adaptive", "21:9", "16:9", "4:3", "1:1", "3:4", "9:16"
    };

    private async Task<VideoOperationStartResult> StartH3VideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new
        {
            type = "unsupported",
            feature = "fps"
        });

        if (request.N is > 1) warnings.Add(new
        {
            type = "unsupported",
            feature = "n"
        });

        if (request.Seed is not null) warnings.Add(new
        {
            type = "unsupported",
            feature = "seed"
        });

        if (request.GenerateAudio is not null) warnings.Add(new
        {
            type = "unsupported",
            feature = "generateAudio"
        });

        var payload = BuildH3VideoPayload(request);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v2/video_generation")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, VideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureH3HttpSuccess(response, raw, "video_generation_v2");

        using var document = JsonDocument.Parse(raw);
        var taskId = document.RootElement.TryGetProperty("task_id", out var taskIdElement)
            && taskIdElement.ValueKind == JsonValueKind.String
            ? taskIdElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("MiniMax H3 video generation returned no task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeMiniMaxVideoV2Operation(taskId, "MiniMax-H3"),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { taskId, status = "queued", apiVersion = "v2" }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private async Task<VideoOperationStatusResult> GetH3VideoOperationStatus(
        MiniMaxVideoOperationData operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var taskId = operation.TaskId;
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v2/query/video_generation/{Uri.EscapeDataString(taskId)}");
        using var httpResponse = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureH3HttpSuccess(httpResponse, raw, "video_generation_v2_query");

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("task", out var task) || task.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("MiniMax H3 query response contained no task.");

        var status = task.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = (operation.Model ?? "MiniMax-H3").ToModelId(GetIdentifier())
        };
        
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            taskId,
            status,
            apiVersion = "v2",
            task = task.Clone()
        }, costs: TryCalculateH3VideoCost(task));

        if (string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
        {
            var error = TryGetH3TaskError(task) ?? $"MiniMax H3 task '{taskId}' ended with status '{status}'.";
            return new VideoOperationErrorResult { Error = error, ProviderMetadata = metadata, Response = response };
        }

        if (!string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = $"MiniMax H3 returned unknown task status '{status}'.", ProviderMetadata = metadata, Response = response };

        var url = task.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("url", out var urlElement)
            && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult { Error = $"MiniMax H3 task '{taskId}' succeeded but returned no content.url.", ProviderMetadata = metadata, Response = response };

        using var videoResponse = await _client.GetAsync(url, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"MiniMax H3 video download failed ({(int)videoResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? GuessVideoMediaType(url) ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildH3VideoPayload(VideoRequest request)
    {
        var prompt = request.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("MiniMax-H3 requires a non-empty prompt.", nameof(request));
        if (prompt.Length > 7000)
            throw new ArgumentException("MiniMax-H3 prompt must not exceed 7000 characters.", nameof(request));

        var content = new List<object> { new { type = "text", text = prompt } };
        var frameImages = request.FrameImages?.ToList() ?? [];
        if (request.Image is not null)
            frameImages.Insert(0, new VideoFrameImage { FrameType = "first_frame", Image = request.Image });
        var references = request.InputReferences?.ToList() ?? [];
        if (frameImages.Count > 0 && references.Count > 0)
            throw new ArgumentException("MiniMax-H3 frame images and reference inputs are mutually exclusive.", nameof(request));

        var hasFirst = false;
        var hasLast = false;
        foreach (var frame in frameImages)
        {
            var role = IsMiniMaxFirstFrame(frame.FrameType) ? "first_frame"
                : IsMiniMaxLastFrame(frame.FrameType) ? "last_frame"
                : throw new ArgumentException($"Unsupported MiniMax-H3 frame type '{frame.FrameType}'.", nameof(request));
            if (role == "first_frame" && hasFirst || role == "last_frame" && hasLast)
                throw new ArgumentException($"MiniMax-H3 supports only one {role} image.", nameof(request));
            hasFirst |= role == "first_frame";
            hasLast |= role == "last_frame";
            content.Add(new { type = "image_url", image_url = new { url = NormalizeH3Media(frame.Image) }, role });
        }
        if (hasLast && !hasFirst)
            throw new ArgumentException("MiniMax-H3 last_frame must be paired with first_frame.", nameof(request));

        foreach (var reference in references)
        {
            var mediaType = reference.MediaType?.Trim().ToLowerInvariant();
            var kind = mediaType?.Split('/')[0];
            var (type, role) = kind switch
            {
                "image" => ("image_url", "reference_image"),
                "video" => ("video_url", "reference_video"),
                "audio" => ("audio_url", "reference_audio"),
                _ => throw new ArgumentException($"MiniMax-H3 input reference mediaType '{reference.MediaType}' must be image, video, or audio.", nameof(request))
            };
            var media = new Dictionary<string, string> { ["url"] = NormalizeH3Media(reference) };
            content.Add(new Dictionary<string, object> { ["type"] = type, [type] = media, ["role"] = role });
        }

        var resolution = (request.Resolution ?? "768P").Trim().ToUpperInvariant();
        if (resolution is not ("768P" or "2K"))
            throw new ArgumentException("MiniMax-H3 resolution must be 768P or 2K.", nameof(request));
        var duration = request.Duration ?? 5;
        if (duration is < 4 or > 15)
            throw new ArgumentException("MiniMax-H3 duration must be between 4 and 15 seconds.", nameof(request));

        var ratio = request.AspectRatio?.Trim();
        if (frameImages.Count > 0)
            ratio = "adaptive";
        else if (string.IsNullOrWhiteSpace(ratio))
            ratio = references.Count > 0 ? "adaptive" : "16:9";
        if (!H3Ratios.Contains(ratio))
            throw new ArgumentException("MiniMax-H3 aspectRatio is invalid.", nameof(request));
        if (frameImages.Count == 0 && references.Count == 0 && string.Equals(ratio, "adaptive", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MiniMax-H3 text-to-video requires a concrete aspectRatio.", nameof(request));

        return new Dictionary<string, object?>
        {
            ["model"] = "MiniMax-H3",
            ["content"] = content,
            ["resolution"] = resolution,
            ["duration"] = duration,
            ["ratio"] = ratio
        };
    }

    private static string NormalizeH3Media(VideoFile media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (string.IsNullOrWhiteSpace(media.Data))
            throw new ArgumentException("MiniMax-H3 media data is required.", nameof(media));
        var data = media.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("mm_file://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data;
        if (string.IsNullOrWhiteSpace(media.MediaType))
            throw new ArgumentException("MiniMax-H3 mediaType is required for raw base64 data.", nameof(media));
        return data.ToDataUrl(media.MediaType.ToLowerInvariant());
    }

    private static string? TryGetH3TaskError(JsonElement task)
    {
        if (!task.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return null;
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : null;
        var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
        return string.IsNullOrWhiteSpace(message) ? null : $"MiniMax H3 failed (code={code}): {message}";
    }

    private static decimal? TryCalculateH3VideoCost(JsonElement task)
    {
        if (!task.TryGetProperty("model", out var modelElement)
            || modelElement.ValueKind != JsonValueKind.String
            || !string.Equals(modelElement.GetString(), "MiniMax-H3", StringComparison.OrdinalIgnoreCase)
            || !task.TryGetProperty("resolution", out var resolutionElement)
            || resolutionElement.ValueKind != JsonValueKind.String)
            return null;

        var pricePerSecond = resolutionElement.GetString()?.ToUpperInvariant() switch
        {
            "768P" => H3PricePerSecond768P,
            "2K" => H3PricePerSecond2K,
            _ => (decimal?)null
        };
        if (pricePerSecond is null
            || !task.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !TryGetNonNegativeDecimal(usage, "output_seconds", out var outputSeconds)
            || !TryGetNonNegativeDecimal(usage, "input_seconds", out var inputSeconds)
            || !TryGetNonNegativeInt32(usage, "input_image_count", out var inputImageCount))
            return null;

        var billableImageCount = Math.Max(inputImageCount - H3FreeImageCount, 0);
        return ((outputSeconds + inputSeconds) * pricePerSecond.Value)
            + (billableImageCount * H3AdditionalImagePrice);
    }

    private static bool TryGetNonNegativeDecimal(JsonElement source, string propertyName, out decimal value)
    {
        value = 0m;
        return source.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDecimal(out value)
            && value >= 0m;
    }

    private static bool TryGetNonNegativeInt32(JsonElement source, string propertyName, out int value)
    {
        value = 0;
        return source.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value >= 0;
    }

    private static void EnsureH3HttpSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var requestId = root.TryGetProperty("request_id", out var requestIdElement) ? requestIdElement.GetString() : null;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var type = error.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                var message = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : raw;
                throw new InvalidOperationException($"MiniMax {operation} failed ({(int)response.StatusCode}, type={type}, request_id={requestId}): {message}");
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw response error.
        }
        throw new InvalidOperationException($"MiniMax {operation} failed ({(int)response.StatusCode}): {raw}");
    }
}
