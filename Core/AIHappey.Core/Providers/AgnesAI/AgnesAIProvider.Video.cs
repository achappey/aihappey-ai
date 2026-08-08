using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AgnesAI;

public partial class AgnesAIProvider
{
    private const string AgnesVideoOperationTokenPrefix = "agv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildAgnesVideoPayload(request, metadata, warnings);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AgnesJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agnes video create failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var videoId = createRoot.TryGetString("video_id");
        var taskId = createRoot.TryGetString("task_id", "id");

        if (string.IsNullOrWhiteSpace(videoId))
            throw new InvalidOperationException("Agnes video create response missing 'video_id'.");

        return new VideoOperationStartResult
        {
            Operation = EncodeAgnesVideoOperation(videoId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                videoId,
                taskId,
                status = createRoot.TryGetString("status") ?? "queued",
                create = createRoot
            }),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeAgnesVideoOperation(operation);
        ApplyAuthHeader();

        var path = $"agnesapi?video_id={Uri.EscapeDataString(operationData.VideoId)}";
        if (!string.IsNullOrWhiteSpace(operationData.Model))
            path += $"&model_name={Uri.EscapeDataString(operationData.Model)}";

        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, path);
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Agnes video poll failed ({(int)pollResponse.StatusCode}): {pollRaw}");

        using var pollDocument = JsonDocument.Parse(pollRaw);
        var root = pollDocument.RootElement.Clone();
        var status = root.TryGetString("status") ?? "queued";
        var modelId = string.IsNullOrWhiteSpace(operationData.Model)
            ? GetIdentifier()
            : operationData.Model.ToModelId(GetIdentifier());
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = modelId
        };
        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            videoId = operationData.VideoId,
            taskId = root.TryGetString("task_id", "id"),
            status,
            progress = TryGetAgnesVideoProgress(root),
            retrieve = root
        });

        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Agnes video generation failed (video_id={operationData.VideoId}): {GetAgnesVideoError(root)}",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var videoUrl = TryGetAgnesVideoMetadataUrl(root);
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"Agnes video task completed but metadata.url was missing (video_id={operationData.VideoId}).",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var (bytes, mediaType) = await DownloadAgnesBinaryAsync(
            videoUrl,
            GuessAgnesVideoMediaType(videoUrl) ?? "video/mp4",
            cancellationToken);

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = providerMetadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildAgnesVideoPayload(
        VideoRequest request,
        JsonElement metadata,
        List<object> warnings)
    {
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration", details = "Agnes video generation uses num_frames and frame_rate." });

        if (request.N is not null)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.GenerateAudio is not null)
            warnings.Add(new { type = "unsupported", feature = "generateAudio" });

        var payload = CreateAgnesPayload(
            metadata,
            "mode",
            "image",
            "images",
            "image_url",
            "imageUrl",
            "image_urls",
            "imageUrls",
            "extra_body",
            "extraBody",
            "poll_interval_seconds",
            "pollIntervalSeconds",
            "poll_timeout_minutes",
            "pollTimeoutMinutes",
            "poll_max_attempts",
            "pollMaxAttempts");

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;

        if (request.Fps is not null)
            payload["frame_rate"] = request.Fps.Value;

        var resolvedSize = ResolveAgnesVideoSize(request, metadata, warnings);
        if (resolvedSize is { } size)
        {
            payload["width"] = size.width;
            payload["height"] = size.height;
        }

        var extraBody = CreateAgnesExtraBody(metadata, "image", "images", "image_urls", "imageUrls", "mode");
        var imageUrls = ResolveAgnesVideoInputUrls(request, metadata, warnings);
        var mode = ResolveAgnesVideoMode(metadata);

        if (imageUrls.Count == 1 && extraBody.Count == 0 && !string.Equals(mode, "keyframes", StringComparison.OrdinalIgnoreCase))
            payload["image"] = imageUrls[0];
        else if (imageUrls.Count > 0)
            extraBody["image"] = imageUrls;

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (extraBody.Count > 0 || string.Equals(mode, "keyframes", StringComparison.OrdinalIgnoreCase) || imageUrls.Count > 1)
                extraBody["mode"] = mode;
            else
                payload["mode"] = mode;
        }

        if (extraBody.Count > 0)
            payload["extra_body"] = extraBody;

        return payload;
    }

    private static string EncodeAgnesVideoOperation(string videoId, string model)
    {
        var json = JsonSerializer.Serialize(new AgnesVideoOperationData(videoId, model), AgnesJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return AgnesVideoOperationTokenPrefix + base64Url;
    }

    private static AgnesVideoOperationData DecodeAgnesVideoOperation(string operation)
    {
        if (!operation.StartsWith(AgnesVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new AgnesVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[AgnesVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<AgnesVideoOperationData>(json, AgnesJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.VideoId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Agnes video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Agnes video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The Agnes video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static int? TryGetAgnesVideoProgress(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("progress", out var progress)
            && progress.ValueKind == JsonValueKind.Number
            && progress.TryGetInt32(out var value)
                ? value
                : null;

    private static string? TryGetAgnesVideoMetadataUrl(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
                ? metadata.TryGetString("url")
                : null;

    private sealed record AgnesVideoOperationData(string VideoId, string? Model);
}
