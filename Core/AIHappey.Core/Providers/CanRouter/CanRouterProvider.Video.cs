using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.CanRouter;

public partial class CanRouterProvider
{
    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ApplyAuthHeader();
        var payload = new Dictionary<string, object?> { ["model"] = request.Model, ["prompt"] = request.Prompt, ["seconds"] = request.Duration?.ToString() };
        using var response = await _client.PostAsync("v1/videos/generations", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"CanRouter video create failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = ReadVideoString(root, "id") ?? ReadVideoString(root, "request_id") ?? ReadVideoString(root, "task_id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("CanRouter video create response contained no id.");
        return new VideoOperationStartResult { Operation = id, Warnings = [], ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root), Response = new() { Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(GetIdentifier()) } };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ApplyAuthHeader();
        using var response = await _client.GetAsync($"v1/videos/generations/{Uri.EscapeDataString(operation)}", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"CanRouter video poll failed ({(int)response.StatusCode}): {raw}");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadVideoString(root, "status") ?? "unknown";
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var header = new HeaderResponseData { Timestamp = DateTime.UtcNow, Headers = response.GetHeaders(), ModelId = (ReadVideoString(root, "model") ?? GetIdentifier()).ToModelId(GetIdentifier()) };
        if (status is "failed" or "error" or "cancelled") return new VideoOperationErrorResult { Error = $"CanRouter video generation failed with status '{status}'.", ProviderMetadata = metadata, Response = header };
        if (status is not "completed" and not "succeeded") return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = header };
        var url = ReadVideoString(root, "video_url") ?? ReadVideoString(root, "url");
        if (string.IsNullOrWhiteSpace(url)) return new VideoOperationErrorResult { Error = "CanRouter completed the video without a result URL.", ProviderMetadata = metadata, Response = header };
        using var videoResponse = await _client.GetAsync(url, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode) throw new InvalidOperationException($"CanRouter video download failed ({(int)videoResponse.StatusCode}).");
        return new VideoOperationCompletedResult { Videos = [new() { Type = "base64", MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4", Data = Convert.ToBase64String(bytes) }], Warnings = [], ProviderMetadata = metadata, Response = header };
    }

    private static string? ReadVideoString(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
