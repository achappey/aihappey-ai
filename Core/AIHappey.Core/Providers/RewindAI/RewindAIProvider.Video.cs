using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.RewindAI;

public partial class RewindAIProvider
{
    private const string RewindAIVideoOperationTokenPrefix = "rwv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var warnings = BuildRewindAIVideoWarnings(request);
        var payload = BuildRewindAIVideoPayload(request);
        var requestBody = JsonSerializer.Serialize(payload, RewindAIJson);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generate-async")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI video submission failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var jobId = ReadRewindAIVideoJobId(createRoot);
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("RewindAI video submission response did not contain a job id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeRewindAIVideoOperation(jobId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(createRoot),
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

        var (jobId, model) = DecodeRewindAIVideoOperation(operation);
        ApplyAuthHeader();
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI video status failed ({(int)statusResponse.StatusCode}): {statusRaw}");

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var root = statusDocument.RootElement.Clone();
        var status = ReadRewindAIVideoStatus(root);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (IsRewindAIVideoFailure(status))
        {
            return new VideoOperationErrorResult
            {
                Error = ReadRewindAIVideoError(root, jobId),
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!IsRewindAIVideoSuccess(status))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videos = await ExtractRewindAIVideosAsync(root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"RewindAI video job '{jobId}' completed without video output.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos.Select(video => new VideoOperationVideoData
            {
                Type = "base64",
                Data = video.Data,
                MediaType = video.MediaType
            }),
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Dictionary<string, object?> BuildRewindAIVideoPayload(VideoRequest request)
    {
        Dictionary<string, object?> payload = new(StringComparer.Ordinal);
        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var metadata)
            && metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetRewindAIVideoStandardField(payload, "duration", request.Duration is null ? null : $"{request.Duration}s");
        SetRewindAIVideoStandardField(payload, "aspectRatio", request.AspectRatio);
        SetRewindAIVideoStandardField(payload, "resolution", request.Resolution);
        SetRewindAIVideoStandardField(payload, "seed", request.Seed);
        SetRewindAIVideoStandardField(payload, "n", request.N);
        SetRewindAIVideoStandardField(payload, "fps", request.Fps);
        SetRewindAIVideoStandardField(payload, "generateAudio", request.GenerateAudio);
        return payload;
    }

    private static void SetRewindAIVideoStandardField(
        Dictionary<string, object?> payload,
        string name,
        object? value)
    {
        if (value is not null)
            payload[name] = value;
    }

    private static List<object> BuildRewindAIVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "image",
                message = "RewindAI's documented video endpoint supports text-to-video only."
            });
        }

        return warnings;
    }

    private static string EncodeRewindAIVideoOperation(string jobId, string model)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["jobId"] = jobId,
            ["model"] = model
        }, RewindAIJson);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return RewindAIVideoOperationTokenPrefix + encoded;
    }

    private static (string JobId, string Model) DecodeRewindAIVideoOperation(string operation)
    {
        if (!operation.StartsWith(RewindAIVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware RewindAI video operation token is required.", nameof(operation));

        try
        {
            var encoded = operation[RewindAIVideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var jobId = ReadRewindAIString(root, "jobId");
            var model = ReadRewindAIString(root, "model");
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The RewindAI video operation token is invalid.", nameof(operation));

            return (jobId, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The RewindAI video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string ReadRewindAIVideoJobId(JsonElement root)
    {
        var jobId = ReadRewindAIString(root, "id", "jobId", "job_id");
        if (string.IsNullOrWhiteSpace(jobId) && root.TryGetProperty("job", out var job))
            jobId = ReadRewindAIString(job, "id", "jobId", "job_id");
        return jobId;
    }

    private static string ReadRewindAIVideoStatus(JsonElement root)
    {
        var status = ReadRewindAIString(root, "status", "state");
        if (string.IsNullOrWhiteSpace(status) && root.TryGetProperty("job", out var job))
            status = ReadRewindAIString(job, "status", "state");
        return status;
    }

    private static bool IsRewindAIVideoSuccess(string status)
        => status.Equals("completed", StringComparison.OrdinalIgnoreCase)
           || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
           || status.Equals("success", StringComparison.OrdinalIgnoreCase);

    private static bool IsRewindAIVideoFailure(string status)
        => status.Equals("failed", StringComparison.OrdinalIgnoreCase)
           || status.Equals("error", StringComparison.OrdinalIgnoreCase)
           || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
           || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);

    private static string ReadRewindAIVideoError(JsonElement root, string jobId)
    {
        var message = ReadRewindAIString(root, "error", "message", "error_message", "detail");
        if (string.IsNullOrWhiteSpace(message) && root.TryGetProperty("error", out var error))
            message = ReadRewindAIString(error, "message", "detail", "error");
        if (string.IsNullOrWhiteSpace(message) && root.TryGetProperty("job", out var job))
            message = ReadRewindAIString(job, "error", "message", "error_message", "detail");
        return string.IsNullOrWhiteSpace(message)
            ? $"RewindAI video generation failed for job '{jobId}'."
            : message;
    }

    private async Task<List<VideoResponseFile>> ExtractRewindAIVideosAsync(
        JsonElement job,
        CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];
        List<(string Url, string MediaType)> urls = [];
        CollectRewindAIVideoValues(job, videos, urls);

        foreach (var (url, mediaType) in urls.DistinctBy(value => value.Url, StringComparer.Ordinal))
            videos.Add(await DownloadRewindAIVideoAsync(url, mediaType, cancellationToken));

        return videos;
    }

    private static void CollectRewindAIVideoValues(
        JsonElement element,
        List<VideoResponseFile> videos,
        List<(string Url, string MediaType)> urls)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectRewindAIVideoValues(item, videos, urls);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var mediaType = ReadRewindAIString(element, "mime_type", "media_type", "content_type");
        foreach (var name in new[] { "video_url", "download_url", "content_url", "output_url", "url", "video", "output", "result", "data" })
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                AddRewindAIVideoValue(videos, urls, value.GetString(), mediaType);
            else if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectRewindAIVideoValues(value, videos, urls);
        }
    }

    private static void AddRewindAIVideoValue(
        List<VideoResponseFile> videos,
        List<(string Url, string MediaType)> urls,
        string? value,
        string mediaType)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (IsRewindAIAbsoluteUrl(value))
        {
            urls.Add((value, mediaType));
            return;
        }

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = value.IndexOf(',');
            if (separator > 0)
            {
                var declaredType = value[5..separator].Split(';', 2)[0];
                videos.Add(new VideoResponseFile { Data = value[(separator + 1)..], MediaType = declaredType });
            }
            return;
        }

        try
        {
            _ = Convert.FromBase64String(value);
            videos.Add(new VideoResponseFile
            {
                Data = value,
                MediaType = string.IsNullOrWhiteSpace(mediaType) ? "video/mp4" : mediaType
            });
        }
        catch (FormatException)
        {
            // The field was not binary video content.
        }
    }

    private async Task<VideoResponseFile> DownloadRewindAIVideoAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RewindAI video download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = response.Content.Headers.ContentType?.MediaType
                ?? (string.IsNullOrWhiteSpace(fallbackMediaType) ? "video/mp4" : fallbackMediaType)
        };
    }
}
