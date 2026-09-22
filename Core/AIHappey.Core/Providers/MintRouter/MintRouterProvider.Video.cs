using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.MintRouter;

public partial class MintRouterProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["seconds"] = request.Duration?.ToString()
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/videos")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(message, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MintRouter video create failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var id = root.TryGetProperty("request_id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("MintRouter video create response contained no request_id.");

        return new VideoOperationStartResult
        {
            Operation = id,
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/videos/{Uri.EscapeDataString(operation)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MintRouter video poll failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "unknown" : "unknown";
        var model = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var header = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = response.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(model) ? GetIdentifier() : model.ToModelId(GetIdentifier())
        };

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase) || status.Equals("error", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationErrorResult { Error = $"MintRouter video generation failed with status '{status}'.", ProviderMetadata = metadata, Response = header };

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase) && !status.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = header };

        var url = root.TryGetProperty("video_url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult { Error = "MintRouter completed the video without a video_url.", ProviderMetadata = metadata, Response = header };

        using var videoResponse = await _client.GetAsync(url, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"MintRouter video download failed ({(int)videoResponse.StatusCode}).");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4", Data = Convert.ToBase64String(bytes) }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = header
        };
    }
}
