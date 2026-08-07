using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.SpaceXAI;

public partial class SpaceXAIProvider
{
    private static readonly JsonSerializerOptions VideoJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        var warnings = GetXaiVideoWarnings(request);
        var providerOptions = GetXaiVideoProviderOptions(request);
        var payload = BuildXaiVideoPayloadCore(request, providerOptions);
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

        return new VideoOperationStartResult
        {
            Operation = requestId,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                requestId,
                status = "pending"
            }),
            Response = CreateXaiVideoResponseData(request.Model, now)
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        ApplyAuthHeader();
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"v1/videos/{Uri.EscapeDataString(operation)}");
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new Exception(string.IsNullOrWhiteSpace(pollRaw) ? pollResp.ReasonPhrase : pollRaw);

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement;
        var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
        var model = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;
        var response = CreateXaiVideoResponseData(model, DateTime.UtcNow);

        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(status))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = CreateXaiVideoStatusMetadata(operation, status, root),
                Response = response
            };
        }

        if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
        {
            var videoUrl = TryGetVideoUrl(root);
            if (string.IsNullOrWhiteSpace(videoUrl))
                return CreateXaiVideoError(operation, "xAI video result contained no video url.", model, root);

            var videoBytes = await _client.GetByteArrayAsync(videoUrl, cancellationToken);
            return new VideoOperationCompletedResult
            {
                Videos =
                [
                    new VideoOperationVideoData
                    {
                        Type = "base64",
                        MediaType = "video/mp4",
                        Data = Convert.ToBase64String(videoBytes)
                    }
                ],
                Warnings = [],
                ProviderMetadata = CreateXaiVideoStatusMetadata(operation, status, root),
                Response = response
            };
        }

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return CreateXaiVideoError(operation, CreateXaiVideoFailure(operation, root).Message, model, root);

        if (string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase))
            return CreateXaiVideoError(operation, $"xAI video request '{operation}' expired before completion.", model, root);

        return CreateXaiVideoError(operation, $"xAI video request '{operation}' returned unknown status '{status}'.", model, root);
    }

    private static List<object> GetXaiVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });

        return warnings;
    }

    private HeaderResponseData CreateXaiVideoResponseData(string? model, DateTime timestamp)
        => new()
        {
            Timestamp = timestamp,
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

    private VideoOperationErrorResult CreateXaiVideoError(
        string operation,
        string error,
        string? model,
        JsonElement root)
        => new()
        {
            Error = error,
            ProviderMetadata = CreateXaiVideoStatusMetadata(operation, root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null, root),
            Response = CreateXaiVideoResponseData(model, DateTime.UtcNow)
        };

    private Dictionary<string, JsonElement> CreateXaiVideoStatusMetadata(string requestId, string? status, JsonElement root)
    {
        JsonElement? usageClone = null;
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            usageClone = usageEl.Clone();

        JsonElement? videoWithoutUrl = null;
        if (root.TryGetProperty("video", out var videoEl) && videoEl.ValueKind == JsonValueKind.Object)
        {
            var videoMetadata = new Dictionary<string, object?>();
            foreach (var property in videoEl.EnumerateObject())
            {
                if (!string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase))
                    videoMetadata[property.Name] = property.Value.Clone();
            }

            videoWithoutUrl = JsonSerializer.SerializeToElement(videoMetadata, JsonSerializerOptions.Web);
        }

        decimal? cost = null;
        if (root.TryGetProperty("usage", out var gatewayUsageEl)
            && gatewayUsageEl.ValueKind == JsonValueKind.Object
            && gatewayUsageEl.TryGetProperty("cost_in_usd_ticks", out var costTicksEl)
            && costTicksEl.ValueKind == JsonValueKind.Number
            && costTicksEl.TryGetDecimal(out var costTicks))
        {
            cost = costTicks / UsdTicksPerDollar;
        }

        return GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            requestId,
            status,
            model = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null,
            usage = usageClone,
            video = videoWithoutUrl
        }, cost);
    }

    private static Dictionary<string, object?> BuildXaiVideoPayload(VideoRequest request)
        => BuildXaiVideoPayloadCore(request, GetXaiVideoProviderOptions(request));

    private static Dictionary<string, object?> BuildXaiVideoPayloadCore(VideoRequest request, JsonElement providerOptions)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = CreateXaiVideoProviderPassthrough(providerOptions);

        // Standard VideoRequest fields are authoritative when they are available.
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.Duration is not null)
            payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        if (request.Image is not null)
            payload["image"] = ToXaiVideoImage(request.Image);

        var referenceImages = request.InputReferences?.Select(ToXaiVideoImage).ToList() ?? [];
        if (referenceImages.Count > 0)
            payload["reference_images"] = referenceImages;

        return payload;
    }

    private static Dictionary<string, object?> CreateXaiVideoProviderPassthrough(JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private static JsonElement GetXaiVideoProviderOptions(VideoRequest request)
    {
        if (request.ProviderOptions is null
            || !request.ProviderOptions.TryGetValue(SpaceXAIRequestExtensions.SpaceXAIIdentifier, out var providerOptions))
        {
            return default;
        }

        return providerOptions;
    }

    private static Exception CreateXaiVideoFailure(string requestId, JsonElement root)
    {
        string? code = null;
        string? message = null;

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = error.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString()
                : null;
            message = error.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
                ? messageEl.GetString()
                : null;
        }

        var detail = !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(message)
            ? $" [{code}]: {message}"
            : !string.IsNullOrWhiteSpace(message) ? $": {message}" : ".";

        return new InvalidOperationException($"xAI video request '{requestId}' failed{detail}");
    }

    private static Dictionary<string, object?> ToXaiVideoImage(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        return new Dictionary<string, object?>
        {
            ["url"] = NormalizeXaiVideoImageUrl(image)
        };
    }

    private static string NormalizeXaiVideoImageUrl(VideoFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new InvalidOperationException("xAI video image data is required.");

        var data = image.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return data;
        }

        var mediaType = string.IsNullOrWhiteSpace(image.MediaType)
            ? "image/png"
            : image.MediaType;

        return data.ToDataUrl(mediaType);
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
