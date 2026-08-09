using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider
{
    private const string VideoOperationPrefix = "dav2_";
    private sealed record VideoOperationData(string RequestId, string? Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Image is not null)
            throw new NotSupportedException("DeAPI v2 image-to-video is not documented by the supplied API contract.");

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model
        };
        if (request.Seed is not null) payload["seed"] = request.Seed;
        if (request.Fps is not null) payload["fps"] = request.Fps;
        if (request.Duration is not null) payload["frames"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.Resolution) || !string.IsNullOrWhiteSpace(request.AspectRatio))
        {
            var size = ResolveVideoSize(request.Resolution, request.AspectRatio);
            payload["width"] = size.width;
            payload["height"] = size.height;
        }
        MergeProviderMetadata(payload, metadata);

        var requestId = await SubmitJsonJobAsync("api/v2/videos/generations", payload, cancellationToken);
        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(requestId, request.Model),
            Warnings = request.N is > 1 ? [new { type = "unsupported", feature = "n" }] : [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                request_id = requestId,
                status = "pending"
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        ApplyAuthHeader();
        var operationData = DecodeVideoOperation(operation);
        var data = await GetJobAsync(operationData.RequestId, cancellationToken);
        var status = data.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        var metadata = new Dictionary<string, JsonElement> { [GetIdentifier()] = data.Clone() };
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            var error = data.TryGetProperty("error_message", out var message) ? message.GetString()
                : data.TryGetProperty("error_reason", out var reason) ? reason.GetString() : "Unknown DeAPI video error";
            return new VideoOperationErrorResult
            {
                Error = error ?? "Unknown DeAPI video error",
                ProviderMetadata = metadata,
                Response = response
            };
        }
        if (!string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        var resultUrl = GetResultUrl(data);
        if (string.IsNullOrWhiteSpace(resultUrl))
            return new VideoOperationErrorResult
            {
                Error = $"DeAPI video job '{operationData.RequestId}' completed without result_url.",
                ProviderMetadata = metadata,
                Response = response
            };

        var (bytes, mediaType) = await DownloadResultAsync(resultUrl, "video/mp4", cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData {
                Type = "base64", MediaType = mediaType, Data = Convert.ToBase64String(bytes) }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Task<VideoResponse> DeapiVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("DeAPI video generation is asynchronous. Use StartVideoOperation and GetVideoOperationStatus.");

    private static string EncodeVideoOperation(string requestId, string model)
    {
        var json = JsonSerializer.Serialize(new VideoOperationData(requestId, model), DeapiJson);
        return VideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static VideoOperationData DecodeVideoOperation(string operation)
    {
        if (!operation.StartsWith(VideoOperationPrefix, StringComparison.Ordinal))
            return new VideoOperationData(Uri.UnescapeDataString(operation), null);

        try
        {
            var encoded = operation[VideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var data = JsonSerializer.Deserialize<VideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), DeapiJson);
            return data is not null && !string.IsNullOrWhiteSpace(data.RequestId) && !string.IsNullOrWhiteSpace(data.Model)
                ? data : throw new ArgumentException("The DeAPI video operation token is invalid.", nameof(operation));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The DeAPI video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static (int width, int height) ResolveVideoSize(string? resolution, string? aspectRatio)
    {
        if (TryParseSize(resolution, out var width, out var height)) return (width, height);
        if (!string.IsNullOrWhiteSpace(aspectRatio))
        {
            var inferred = aspectRatio.InferSizeFromAspectRatio(256, 1536, 256, 1536);
            if (inferred is not null) return inferred.Value;
        }
        return (512, 512);
    }
}
