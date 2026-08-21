using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ModelMax;

public partial class ModelMaxProvider
{
    private const string ModelMaxVideoOperationTokenPrefix = "mxv1_";
    private static readonly JsonSerializerOptions ModelMaxVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = CopyModelMaxJsonObject(metadata);
        var parameters = GetModelMaxParameters(payload);
        payload["prompt"] = request.Prompt;
        payload["parameters"] = parameters;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            parameters["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["resolution"] = request.Resolution;
        if (request.Duration is not null)
            parameters["duration_seconds"] = request.Duration.Value;
        if (request.GenerateAudio is not null)
            parameters["generate_audio"] = request.GenerateAudio.Value;
        if (request.N is not null)
            parameters["sample_count"] = request.N.Value;
        if (request.Image is not null)
            parameters["image"] = NormalizeModelMaxVideoImage(request.Image);

        var references = request.InputReferences?.ToList() ?? [];
        if (request.Image is null && references.Count > 0)
            parameters["image"] = NormalizeModelMaxVideoImage(references[0]);
        if (references.Count > (request.Image is null ? 1 : 0))
            warnings.Add(new { type = "unsupported", feature = "multiple_input_references", details = "ModelMax documents one parameters.image value." });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        var routeModel = Uri.EscapeDataString(request.Model);
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/queue/{routeModel}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ModelMaxVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModelMax video submission failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var root = createDocument.RootElement.Clone();
        var requestId = TryGetModelMaxString(root, "request_id");
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("ModelMax video submission returned no request_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeModelMaxVideoOperation(requestId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeModelMaxVideoOperation(operation);
        ApplyAuthHeader();

        if (string.IsNullOrWhiteSpace(operationData.Model))
            throw new ArgumentException("Legacy ModelMax video operation IDs do not contain the model required by the status route. Start a new operation to obtain an opaque model-aware token.", nameof(operation));

        var routeModel = Uri.EscapeDataString(operationData.Model);
        var requestId = Uri.EscapeDataString(operationData.RequestId);
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/queue/{routeModel}/requests/{requestId}/status");
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModelMax video status failed ({(int)statusResponse.StatusCode}): {statusRaw}");

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var statusRoot = statusDocument.RootElement.Clone();
        var status = TryGetModelMaxString(statusRoot, "status")?.Trim().ToUpperInvariant();
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status == "FAILED")
            return new VideoOperationErrorResult
            {
                Error = TryGetModelMaxString(statusRoot, "error") ?? $"ModelMax video generation failed (request_id={operationData.RequestId}).",
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot),
                Response = response
            };

        if (status != "COMPLETED")
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(statusRoot),
                Response = response
            };

        using var resultRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/queue/{routeModel}/requests/{requestId}");
        using var resultResponse = await _client.SendAsync(resultRequest, cancellationToken);
        var resultRaw = await resultResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!resultResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"ModelMax video result failed ({(int)resultResponse.StatusCode}): {resultRaw}");

        using var resultDocument = JsonDocument.Parse(resultRaw);
        var resultRoot = resultDocument.RootElement.Clone();
        var resultStatus = TryGetModelMaxString(resultRoot, "status")?.Trim().ToUpperInvariant();
        response.Headers = resultResponse.GetHeaders();

        if (resultStatus == "FAILED")
            return new VideoOperationErrorResult
            {
                Error = TryGetModelMaxString(resultRoot, "error") ?? $"ModelMax video generation failed (request_id={operationData.RequestId}).",
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(resultRoot),
                Response = response
            };
        if (resultStatus != "COMPLETED")
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(resultRoot),
                Response = response
            };

        var videos = await DownloadModelMaxVideosAsync(resultRoot, cancellationToken);
        if (videos.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"ModelMax video task '{operationData.RequestId}' completed without content URLs.",
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(resultRoot),
                Response = response
            };

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(resultRoot),
            Response = response
        };
    }

    private async Task<List<VideoOperationVideoData>> DownloadModelMaxVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var videos = new List<VideoOperationVideoData>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in data.EnumerateArray())
        {
            var url = TryGetModelMaxString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            using var downloadRequest = new HttpRequestMessage(HttpMethod.Get, url);
            using var downloadResponse = await _client.SendAsync(downloadRequest, cancellationToken);
            var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!downloadResponse.IsSuccessStatusCode || bytes.Length == 0)
                throw new InvalidOperationException($"ModelMax video download failed ({(int)downloadResponse.StatusCode}).");

            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = downloadResponse.Content.Headers.ContentType?.MediaType ?? GuessModelMaxVideoMediaType(url) ?? "video/mp4",
                Data = Convert.ToBase64String(bytes)
            });
        }
        return videos;
    }

    private static Dictionary<string, object?> GetModelMaxParameters(Dictionary<string, object?> payload)
    {
        if (payload.TryGetValue("parameters", out var rawParameters) && rawParameters is JsonElement element && element.ValueKind == JsonValueKind.Object)
            return CopyModelMaxJsonObject(element);
        return [];
    }

    private static string NormalizeModelMaxVideoImage(VideoFile image)
    {
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("ModelMax video image data is required.", nameof(image));
        var data = image.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return data;
        return data.RemoveDataUrlPrefix();
    }

    private static string EncodeModelMaxVideoOperation(string requestId, string model)
    {
        var envelope = JsonSerializer.Serialize(new { requestId, model }, ModelMaxVideoJson);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return ModelMaxVideoOperationTokenPrefix + base64Url;
    }

    private static (string RequestId, string? Model) DecodeModelMaxVideoOperation(string operation)
    {
        if (!operation.StartsWith(ModelMaxVideoOperationTokenPrefix, StringComparison.Ordinal))
            return (Uri.UnescapeDataString(operation), null);

        var base64 = operation[ModelMaxVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 != 0)
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            var requestId = TryGetModelMaxString(document.RootElement, "requestId");
            var model = TryGetModelMaxString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The ModelMax video operation token is invalid.", nameof(operation));
            return (requestId, model);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The ModelMax video operation token is invalid.", nameof(operation), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The ModelMax video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? TryGetModelMaxString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GuessModelMaxVideoMediaType(string url)
    {
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : null;
    }
}
