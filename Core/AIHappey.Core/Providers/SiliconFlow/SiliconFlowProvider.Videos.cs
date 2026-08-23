using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider
{
    private const string SiliconFlowVideoOperationTokenPrefix = "sfv1_";

    private static readonly JsonSerializerOptions VideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        AddSiliconFlowVideoWarnings(request, warnings);

        var payload = BuildSiliconFlowVideoPayload(request);
        var submitJson = JsonSerializer.Serialize(payload, VideoJsonOptions);
        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video/submit")
        {
            Content = new StringContent(submitJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"SiliconFlow video submit failed ({(int)submitResponse.StatusCode}): {submitRaw}");

        using var submitDocument = JsonDocument.Parse(submitRaw);
        var submitRoot = submitDocument.RootElement.Clone();
        var requestId = submitRoot.TryGetProperty("requestId", out var requestIdElement)
            && requestIdElement.ValueKind == JsonValueKind.String
                ? requestIdElement.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("SiliconFlow video submit did not return requestId.");

        return new VideoOperationStartResult
        {
            Operation = EncodeSiliconFlowVideoOperation(requestId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(submitRoot),
            Response = new()
            {
                Timestamp = now,
                Headers = submitResponse.GetHeaders(),
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

        var (requestId, model) = DecodeSiliconFlowVideoOperation(operation);
        ApplyAuthHeader();

        var statusJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["requestId"] = requestId
        }, VideoJsonOptions);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Post, "v1/video/status")
        {
            Content = new StringContent(statusJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"SiliconFlow video status failed ({(int)statusResponse.StatusCode}): {statusRaw}");

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var statusRoot = statusDocument.RootElement.Clone();
        var status = statusRoot.TryGetProperty("status", out var statusElement)
            && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : null;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = $"SiliconFlow video generation failed (requestId={requestId}): {TryGetFailReason(statusRoot)}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "Succeed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await DownloadSiliconFlowVideosAsync(statusRoot, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"SiliconFlow video request '{requestId}' succeeded but returned no video URLs.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Dictionary<string, object?> BuildSiliconFlowVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>();
        if (request.ProviderOptions?.TryGetValue(GetIdentifier(), out var providerMetadata) == true
            && providerMetadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerMetadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["image_size"] = request.Resolution ?? "1280x720";

        if (request.Seed is not null)
            payload["seed"] = request.Seed.Value;
        else
            payload.Remove("seed");

        if (request.Image is not null)
            payload["image"] = ToSiliconFlowImageInput(request.Image);
        else
            payload.Remove("image");

        return payload;
    }

    private static void AddSiliconFlowVideoWarnings(VideoRequest request, List<object> warnings)
    {
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "inputReferences" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.GenerateAudio is not null)
            warnings.Add(new { type = "unsupported", feature = "generateAudio" });
    }

    private static string EncodeSiliconFlowVideoOperation(string requestId, string model)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["requestId"] = requestId,
            ["model"] = model
        }, VideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return SiliconFlowVideoOperationTokenPrefix + base64Url;
    }

    private static (string RequestId, string Model) DecodeSiliconFlowVideoOperation(string operation)
    {
        if (!operation.StartsWith(SiliconFlowVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The SiliconFlow video operation token is invalid.", nameof(operation));

        var base64Url = operation[SiliconFlowVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var requestId = root.TryGetProperty("requestId", out var requestIdElement)
                && requestIdElement.ValueKind == JsonValueKind.String
                    ? requestIdElement.GetString()
                    : null;
            var model = root.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The SiliconFlow video operation token is invalid.", nameof(operation));

            return (requestId, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The SiliconFlow video operation token is invalid.", nameof(operation), exception);
        }
    }

    private async Task<List<VideoOperationVideoData>> DownloadSiliconFlowVideosAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var urls = TryGetVideoUrls(root);
        if (urls.Count == 0)
            return [];

        var downloadClient = _factory.CreateClient();
        List<VideoOperationVideoData> videos = [];
        foreach (var videoUrl in urls)
        {
            using var mediaResponse = await downloadClient.GetAsync(videoUrl, cancellationToken);
            var bytes = await mediaResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!mediaResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"SiliconFlow video download failed ({(int)mediaResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = mediaResponse.Content.Headers.ContentType?.MediaType
                    ?? GuessVideoMediaType(videoUrl)
                    ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            });
        }

        return videos;
    }

    private static string TryGetFailReason(JsonElement root)
    {
        if (root.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String)
            return reasonElement.GetString() ?? "Unknown error";
        if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            return messageElement.GetString() ?? "Unknown error";

        return "Unknown error";
    }

    private static List<string> TryGetVideoUrls(JsonElement root)
    {
        List<string> urls = [];
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Object)
            return urls;
        if (!results.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            return urls;

        foreach (var video in videos.EnumerateArray())
        {
            if (!video.TryGetProperty("url", out var urlElement) || urlElement.ValueKind != JsonValueKind.String)
                continue;

            var url = urlElement.GetString();
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url);
        }

        return urls;
    }

    private static string ToSiliconFlowImageInput(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Image data is required.", nameof(file));
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }
        if (!string.IsNullOrWhiteSpace(file.MediaType))
            return $"data:{file.MediaType};base64,{file.Data}";

        return file.Data;
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        return null;
    }
}
