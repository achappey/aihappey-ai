using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private const string OpperAIVideoOperationTokenPrefix = "opv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var providerMetadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var payload = BuildOpperAIVideoPayload(request, providerMetadata);

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v3/videos")
        {
            Content = CreateOpperAIJsonContent(payload)
        };

        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(createRaw)
                ? $"OpperAI video generation failed ({(int)createResponse.StatusCode})."
                : $"OpperAI video generation failed ({(int)createResponse.StatusCode}): {createRaw}");

        using var createDocument = JsonDocument.Parse(createRaw);
        var createRoot = createDocument.RootElement.Clone();
        var taskId = TryGetOpperAIString(createRoot, "id")
            ?? throw new InvalidOperationException("OpperAI video generation returned no id.");
        var statusUrl = TryGetOpperAIString(createRoot, "status_url", "statusUrl")
            ?? throw new InvalidOperationException($"OpperAI video generation returned no status_url (id={taskId}).");

        return new VideoOperationStartResult
        {
            Operation = EncodeOpperAIVideoOperation(taskId, statusUrl, request.Model),
            Warnings = [],
            ProviderMetadata = CreateOpperAIMediaMetadata(new
            {
                endpoint = "v3/videos",
                statusUrl,
                id = taskId,
                create = createRoot
            }),
            Response = new()
            {
                Timestamp = now,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeOpperAIVideoOperation(operation);
        ApplyAuthHeader();

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, operationData.StatusUrl);
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var statusRaw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(statusRaw)
                ? $"OpperAI video status failed ({(int)statusResponse.StatusCode})."
                : $"OpperAI video status failed ({(int)statusResponse.StatusCode}): {statusRaw}");

        using var statusDocument = JsonDocument.Parse(statusRaw);
        var statusRoot = statusDocument.RootElement.Clone();
        var status = TryGetOpperAIString(statusRoot, "status", "state") ?? "unknown";
        var metadata = CreateOpperAIMediaMetadata(statusRoot);
        var response = new HeaderResponseData
        {
            Timestamp = ResolveOpperAITimestamp(statusRoot, DateTime.UtcNow),
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (!IsOpperAITerminalStatus(status))
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        if (!IsOpperAISuccessStatus(status))
            return new VideoOperationErrorResult
            {
                Error = ExtractOpperAIVideoError(statusRoot)
                    ?? $"OpperAI video generation failed with status '{status}' (id={operationData.Id}).",
                ProviderMetadata = metadata,
                Response = response
            };

        var videoUrl = ExtractOpperAIVideoUrl(statusRoot);
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"OpperAI video task '{operationData.Id}' completed but returned no downloadable URL.",
                ProviderMetadata = metadata,
                Response = response
            };

        var downloaded = await DownloadOpperAIMediaAsync(videoUrl, "video/mp4", cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(downloaded.Bytes),
                    MediaType = downloaded.MediaType
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildOpperAIVideoPayload(
        VideoRequest request,
        JsonElement providerMetadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerMetadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerMetadata.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        payload["store"] = false;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.Duration is not null)
            payload["seconds"] = request.Duration.Value;

        if (request.Image is not null)
            payload["image"] = NormalizeOpperAIInputFile(request.Image.Data, request.Image.MediaType);

        if (request.InputReferences?.Any() == true)
        {
            payload["reference_images"] = request.InputReferences
                .Select(reference => NormalizeOpperAIInputFile(reference.Data, reference.MediaType))
                .ToArray();
        }

        if (request.FrameImages?.Any() == true)
        {
            var lastImage = request.FrameImages.FirstOrDefault(frame =>
                string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(frame.FrameType, "last", StringComparison.OrdinalIgnoreCase));

            if (lastImage is not null)
                payload["last_image"] = NormalizeOpperAIInputFile(lastImage.Image.Data, lastImage.Image.MediaType);
        }

        return payload;
    }

    private static string EncodeOpperAIVideoOperation(string id, string statusUrl, string model)
    {
        var envelope = JsonSerializer.Serialize(new { id, statusUrl, model }, OpperAIMediaJsonOptions);
        var base64Url = Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return OpperAIVideoOperationTokenPrefix + base64Url;
    }

    private static (string Id, string StatusUrl, string Model) DecodeOpperAIVideoOperation(string operation)
    {
        if (!operation.StartsWith(OpperAIVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Legacy OpperAI video operation IDs do not contain the model required for an accurate status response. Start a new operation to obtain an opaque model-aware token.", nameof(operation));

        var base64Url = operation[OpperAIVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(base64Url)));
            var root = document.RootElement;
            var id = TryGetOpperAIString(root, "id");
            var statusUrl = TryGetOpperAIString(root, "statusUrl", "status_url");
            var model = TryGetOpperAIString(root, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(statusUrl) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("The OpperAI video operation token is invalid.", nameof(operation));
            return (id, statusUrl, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The OpperAI video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? ExtractOpperAIVideoError(JsonElement root)
        => TryGetOpperAIString(root, "message", "error_message", "errorMessage")
            ?? (TryGetOpperAIProperty(root, "error", out var error)
                ? TryGetOpperAIString(error, "message", "detail", "code") ?? error.ToString()
                : null);
}
