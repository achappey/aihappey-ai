using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MegaNova;

public partial class MegaNovaProvider
{
    private const string MegaNovaVideoOperationTokenPrefix = "mnv1_";

    private static readonly JsonSerializerOptions MegaNovaVideoJsonOptions = new(JsonSerializerDefaults.Web)
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

        List<object> warnings = [];
        var metadata = GetMegaNovaProviderMetadata(request, GetIdentifier());
        var payload = BuildMegaNovaVideoPayload(request, metadata, warnings);
        var createJson = JsonSerializer.Serialize(payload, MegaNovaVideoJsonOptions);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/videos/generations")
        {
            Content = new StringContent(createJson, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"MegaNova video generation failed ({(int)createResponse.StatusCode})."
                : $"MegaNova video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var generationId = TryGetMegaNovaVideoId(createRoot)
            ?? throw new InvalidOperationException("MegaNova video generation returned no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeMegaNovaVideoOperation(generationId, request.Model.Trim()),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                generationId,
                status = TryGetMegaNovaString(createRoot, "status", "state") ?? "submitted",
                create = createRoot
            }),
            Response = new HeaderResponseData
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

        var operationData = DecodeMegaNovaVideoOperation(operation);
        ApplyAuthHeader();

        using var response = await _client.GetAsync(
            $"v1/videos/generations/{Uri.EscapeDataString(operationData.GenerationId)}",
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MegaNova video status failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var status = TryGetMegaNovaString(root, "status", "state") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            generationId = operationData.GenerationId,
            status,
            poll = root
        });
        var responseData = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };

        if (IsMegaNovaVideoFailed(status))
        {
            var error = TryGetMegaNovaString(root, "error", "message", "failure_reason", "failureReason") ?? raw;
            return new VideoOperationErrorResult
            {
                Error = $"MegaNova video generation failed with status '{status}': {error}",
                ProviderMetadata = metadata,
                Response = responseData
            };
        }

        if (!IsMegaNovaVideoCompleted(status))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = responseData };

        using var streamResponse = await _client.GetAsync(
            $"v1/videos/generations/{Uri.EscapeDataString(operationData.GenerationId)}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var videoBytes = await streamResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!streamResponse.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(videoBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"MegaNova video download failed ({(int)streamResponse.StatusCode})."
                : $"MegaNova video download failed ({(int)streamResponse.StatusCode}): {error}");
        }

        responseData.Headers = streamResponse.GetHeaders();
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = streamResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = responseData
        };
    }

    private static string EncodeMegaNovaVideoOperation(string generationId, string model)
    {
        var json = JsonSerializer.Serialize(new MegaNovaVideoOperationData(generationId, model), MegaNovaVideoJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return MegaNovaVideoOperationTokenPrefix + base64Url;
    }

    private static MegaNovaVideoOperationData DecodeMegaNovaVideoOperation(string operation)
    {
        if (!operation.StartsWith(MegaNovaVideoOperationTokenPrefix, StringComparison.Ordinal))
            return new MegaNovaVideoOperationData(Uri.UnescapeDataString(operation), null);

        var base64Url = operation[MegaNovaVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
            var data = JsonSerializer.Deserialize<MegaNovaVideoOperationData>(json, MegaNovaVideoJsonOptions);
            if (data is null || string.IsNullOrWhiteSpace(data.GenerationId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The MegaNova video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The MegaNova video operation token is invalid.", nameof(operation), ex);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The MegaNova video operation token is invalid.", nameof(operation), ex);
        }
    }

    private sealed record MegaNovaVideoOperationData(string GenerationId, string? Model);

    private static Dictionary<string, object?> BuildMegaNovaVideoPayload(VideoRequest request, JsonElement metadata, List<object> warnings)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        MergeMegaNovaProviderMetadata(payload, metadata);

        payload["model"] = request.Model.Trim();
        payload["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (request.Duration is not null)
            payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Fps is not null)
            payload["fps"] = request.Fps;
        if (request.Seed is not null)
            payload["seed"] = request.Seed;
        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Image is not null)
            payload["image"] = NormalizeMegaNovaVideoImage(request.Image);

        if (request.InputReferences?.Any() == true)
            payload["input_references"] = request.InputReferences.Select(NormalizeMegaNovaVideoImage).ToArray();

        if (request.FrameImages?.Any() == true)
            payload["frame_images"] = request.FrameImages.Select(frame => new Dictionary<string, object?>
            {
                ["frame_type"] = frame.FrameType,
                ["image"] = NormalizeMegaNovaVideoImage(frame.Image)
            }).ToArray();

        return payload;
    }

    private static string NormalizeMegaNovaVideoImage(VideoFile file)
    {
        if (file.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return file.Data;
        }

        return file.Data;
    }

    private static string? TryGetMegaNovaVideoId(JsonElement root)
        => TryGetMegaNovaString(root, "id", "generation_id", "generationId", "task_id", "taskId");

    private static bool IsMegaNovaVideoCompleted(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;

        return status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("success", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMegaNovaVideoFailed(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;

        return status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase);
    }
}
