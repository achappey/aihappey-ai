using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OrcaRouter;

public partial class OrcaRouterProvider
{
    private const string VideoGenerationsEndpoint = "v1/video/generations";
    private const string VideoOperationTokenPrefix = "ocv1_";

    private static readonly JsonSerializerOptions VideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

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
        var submittedAt = DateTime.UtcNow;
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildVideoPayload(request, metadata);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, VideoGenerationsEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, VideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw CreateVideoException("submit", createResponse, createRaw);

        using var createDocument = JsonDocument.Parse(createRaw);
        var createResult = createDocument.RootElement.Clone();
        var taskId = GetString(createResult, "task_id") ?? GetString(createResult, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("OrcaRouter video submission response did not contain a task id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(taskId, request.Model),
            Warnings = BuildVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(createResult),
            Response = new()
            {
                Timestamp = submittedAt,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var (taskId, model) = DecodeVideoOperation(operation);
        ApplyAuthHeader();

        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{VideoGenerationsEndpoint}/{Uri.EscapeDataString(taskId)}");
        using var statusResponse = await _client.SendAsync(
            statusRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw CreateVideoException("status", statusResponse, statusRaw);

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var statusResult = statusDocument.RootElement.Clone();
        var data = GetVideoData(statusResult);
        var state = GetVideoState(statusResult);
        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusResult);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = model.ToModelId(GetIdentifier())
        };

        if (IsFailedVideoState(state))
        {
            return new VideoOperationErrorResult
            {
                Error = GetString(data, "fail_reason")
                    ?? GetString(statusResult, "message")
                    ?? $"OrcaRouter video generation failed with status '{state ?? "unknown"}' (task_id={taskId}).",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        if (!IsSuccessfulVideoState(state))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var resultUrl = GetString(data, "result_url")
            ?? GetString(statusResult, "url")
            ?? GetString(statusResult, "video_url");
        if (string.IsNullOrWhiteSpace(resultUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"OrcaRouter video task completed without a result URL (task_id={taskId}).",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var (video, mediaType) = await DownloadVideoAsync(resultUrl, cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(video),
                    MediaType = mediaType
                }
            ],
            Warnings = [],
            ProviderMetadata = providerMetadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        if (request.Image is not null)
            payload["image"] = ToVideoInput(request.Image);

        var providerMetadata = metadata.ValueKind == JsonValueKind.Object
            ? metadata.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        if (!providerMetadata.ContainsKey("metadata"))
        {
            var generatedMetadata = new Dictionary<string, object?>(StringComparer.Ordinal);
            var isMiniMax = request.Model.StartsWith("minimax/", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(request.AspectRatio))
                generatedMetadata[isMiniMax ? "ratio" : request.Model.StartsWith("byteplus/", StringComparison.OrdinalIgnoreCase) ? "ratio" : "aspect_ratio"] = request.AspectRatio;
            if (request.Duration is not null && !isMiniMax)
                generatedMetadata["duration"] = request.Model.StartsWith("kling/", StringComparison.OrdinalIgnoreCase)
                    ? request.Duration.Value.ToString(CultureInfo.InvariantCulture)
                    : request.Duration.Value;
            if (!string.IsNullOrWhiteSpace(request.Resolution) && !isMiniMax)
                generatedMetadata["resolution"] = request.Resolution;
            if (request.Seed is not null)
                generatedMetadata["seed"] = request.Seed;
            if (request.GenerateAudio is not null)
                generatedMetadata["generate_audio"] = request.GenerateAudio;
            if (generatedMetadata.Count > 0)
                providerMetadata["metadata"] = generatedMetadata;
        }

        if (request.Model.StartsWith("minimax/", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Duration is not null)
                payload["duration"] = request.Duration;
            if (!string.IsNullOrWhiteSpace(request.Resolution))
                payload["size"] = request.Resolution;
        }

        foreach (var (key, value) in providerMetadata)
        {
            if (!string.Equals(key, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "prompt", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "image", StringComparison.OrdinalIgnoreCase))
            {
                payload[key] = value;
            }
        }

        return payload;
    }

    private static List<object> BuildVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });
        return warnings;
    }

    private static string EncodeVideoOperation(string taskId, string model)
    {
        var envelope = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["taskId"] = taskId,
            ["model"] = model
        }, VideoJsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.GetRawText()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return VideoOperationTokenPrefix + encoded;
    }

    private static (string TaskId, string Model) DecodeVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));
        if (!operation.StartsWith(VideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The OrcaRouter video operation token is invalid. Start a new operation to obtain a model-aware token.", nameof(operation));

        try
        {
            var encoded = operation[VideoOperationTokenPrefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            var remainder = encoded.Length % 4;
            if (remainder != 0)
                encoded = encoded.PadRight(encoded.Length + 4 - remainder, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var root = document.RootElement;
            var taskId = GetString(root, "taskId");
            var model = GetString(root, "model");
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The OrcaRouter video operation token is invalid.", nameof(operation));

            return (taskId, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The OrcaRouter video operation token is invalid.", nameof(operation), exception);
        }
    }

    private async Task<(byte[] Video, string MimeType)> DownloadVideoAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateVideoException("download", response, error);
        }

        return (
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType ?? "video/mp4");
    }

    private static string ToVideoInput(VideoFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Video image input data is required.", nameof(file));
        if (Uri.TryCreate(file.Data, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return file.Data;
        if (file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? "image/png" : file.MediaType)};base64,{file.Data}";
    }

    private static JsonElement GetVideoData(JsonElement result)
        => result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : result;

    private static string? GetVideoState(JsonElement result)
        => GetString(GetVideoData(result), "status") ?? GetString(result, "status");

    private static bool IsFailedVideoState(string? status)
        => status?.Trim().ToUpperInvariant() is "FAILURE" or "FAILED" or "ERROR" or "CANCELED" or "CANCELLED";

    private static bool IsSuccessfulVideoState(string? status)
        => status?.Trim().ToUpperInvariant() is "SUCCESS" or "SUCCEEDED" or "COMPLETED";

    private static InvalidOperationException CreateVideoException(
        string operation,
        HttpResponseMessage response,
        string content)
        => new(string.IsNullOrWhiteSpace(content)
            ? $"OrcaRouter video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
            : $"OrcaRouter video {operation} request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {content}");
}
