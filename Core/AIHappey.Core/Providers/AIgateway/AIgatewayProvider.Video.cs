using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    private const string AIgatewayVideoOperationTokenPrefix = "aigv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();
        var payload = CreateAIgatewayPayload([], request.ProviderOptions);
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        SetAIgatewayVideoValue(payload, "duration", request.Duration);
        SetAIgatewayVideoValue(payload, "resolution", request.Resolution);
        SetAIgatewayVideoValue(payload, "aspect_ratio", request.AspectRatio);
        SetAIgatewayVideoValue(payload, "seed", request.Seed);
        SetAIgatewayVideoValue(payload, "n", request.N);

        using var createRequest = CreateAIgatewayJsonRequest(HttpMethod.Post, "v1/videos/generations", payload);
        using var createResponse = await _client.SendAsync(createRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var created = await ReadAIgatewayJsonAsync(createResponse, "video generation", cancellationToken);
        var jobId = GetAIgatewayString(created, "id");
        if (string.IsNullOrWhiteSpace(jobId)) throw new InvalidOperationException("AIgateway video generation did not return a job id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeAIgatewayVideoOperation(jobId, request.Model),
            Warnings = GetAIgatewayVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(created),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = createResponse.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var (jobId, model) = DecodeAIgatewayVideoOperation(operation);
        ApplyAuthHeader();
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
        using var statusResponse = await _client.SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var root = await ReadAIgatewayJsonAsync(statusResponse, "video job status", cancellationToken);
        var status = GetAIgatewayString(root, "status");
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = statusResponse.GetHeaders(), ModelId = model.ToModelId(GetIdentifier()) };

        if (status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("canceled", StringComparison.OrdinalIgnoreCase)))
        {
            return new VideoOperationErrorResult
            {
                Error = GetAIgatewayString(root, "error", "message") ?? $"AIgateway video job '{jobId}' failed with status '{status}'.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };

        var fileUrl = GetAIgatewayString(root, "result", "file_url");
        if (string.IsNullOrWhiteSpace(fileUrl))
            return new VideoOperationErrorResult { Error = $"AIgateway video job '{jobId}' completed without result.file_url.", ProviderMetadata = metadata, Response = response };
        var (video, mediaType) = await DownloadAIgatewayFileAsync(fileUrl, ResolveAIgatewayVideoMimeType(fileUrl), cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", Data = Convert.ToBase64String(video), MediaType = mediaType }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static void SetAIgatewayVideoValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null) payload[name] = value;
    }

    private static List<object> GetAIgatewayVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.Image is not null || request.InputReferences?.Any() == true || request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "image inputs" });
        return warnings;
    }

    private static string ResolveAIgatewayVideoMimeType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";

    private static string EncodeAIgatewayVideoOperation(string id, string model)
        => AIgatewayVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = id, ["model"] = model })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static (string Id, string Model) DecodeAIgatewayVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(AIgatewayVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware AIgateway video operation token is required.", nameof(operation));
        try
        {
            var value = operation[AIgatewayVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            var id = GetAIgatewayString(document.RootElement, "id");
            var model = GetAIgatewayString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(model)) throw new ArgumentException("The AIgateway video operation token is invalid.", nameof(operation));
            return (id, model);
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The AIgateway video operation token is invalid.", nameof(operation), exception);
        }
    }
}
