using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Zebracat;

public partial class ZebracatProvider
{
    private const string ZebracatVideoOperationPrefix = "zcv1_";
    private static readonly JsonSerializerOptions ZebracatVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var endpointModel = NormalizeZebracatModel(request.Model).ToLowerInvariant();
        if (endpointModel is not ("idea" or "script" or "blog"))
            throw new ArgumentException($"Unsupported Zebracat video model '{request.Model}'.", nameof(request));

        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });
        if (!string.IsNullOrWhiteSpace(request.Resolution)) warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (request.Image is not null || request.FrameImages?.Any() == true || request.InputReferences?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "image_references" });

        var payload = CopySupportedZebracatOptions(
            request.GetProviderMetadata<JsonElement>(GetIdentifier()),
            ["language", "duration", "voice_id", "footage_type", "style_id", "mood", "aspect_ratio", "prompt_style", "character_id", "brand_id", "avatar_id", "should_render"]);
        payload[endpointModel switch { "idea" => "idea", "script" => "script", _ => "url" }] = request.Prompt;
        if (request.Duration is not null && !payload.ContainsKey("duration")) payload["duration"] = request.Duration;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && !payload.ContainsKey("aspect_ratio")) payload["aspect_ratio"] = MapZebracatAspectRatio(request.AspectRatio);
        if (!payload.ContainsKey("should_render")) payload["should_render"] = true;

        if (!payload.ContainsKey("language"))
            throw new ArgumentException("Zebracat requires providerOptions.zebracat.language.", nameof(request));
        if (!payload.ContainsKey("duration"))
            throw new ArgumentException("Zebracat requires duration.", nameof(request));

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/video/{endpointModel}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, ZebracatVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        ApplyAuthHeader(createRequest);
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var raw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zebracat video generation failed ({(int)createResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var taskId = root.TryGetProperty("task_id", out var taskElement) ? taskElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("Zebracat video generation returned no task_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeZebracatVideoOperation(taskId, request.Model),
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

        var operationData = DecodeZebracatVideoOperation(operation);
        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"v1/video/status?task_id={Uri.EscapeDataString(operationData.TaskId)}");
        ApplyAuthHeader(statusRequest);
        using var statusResponse = await _client.SendAsync(statusRequest, cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!statusResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zebracat video status failed ({(int)statusResponse.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };

        if (status is "failed" or "avatar_render_failed" or "render_failed")
        {
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            return new VideoOperationErrorResult
            {
                Error = string.IsNullOrWhiteSpace(error) ? $"Zebracat video generation failed with status '{status}'." : error,
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videoUrl = root.TryGetProperty("video_url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult { Error = "Zebracat completed the task without a video_url.", ProviderMetadata = metadata, Response = response };

        using var videoResponse = await _client.GetAsync(videoUrl, cancellationToken);
        var bytes = await videoResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!videoResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Zebracat video download failed ({(int)videoResponse.StatusCode}).");

        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(bytes),
                MediaType = videoResponse.Content.Headers.ContentType?.MediaType ?? "video/mp4"
            }],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static string EncodeZebracatVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new ZebracatVideoOperationData(taskId, model), ZebracatVideoJson);
        return ZebracatVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ZebracatVideoOperationData DecodeZebracatVideoOperation(string operation)
    {
        if (!operation.StartsWith(ZebracatVideoOperationPrefix, StringComparison.Ordinal))
            return new ZebracatVideoOperationData(Uri.UnescapeDataString(operation), null);
        var value = operation[ZebracatVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
        if (value.Length % 4 != 0) value = value.PadRight(value.Length + 4 - value.Length % 4, '=');
        try
        {
            var data = JsonSerializer.Deserialize<ZebracatVideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), ZebracatVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskId) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Zebracat video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Zebracat video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string? MapZebracatAspectRatio(string aspectRatio) => aspectRatio.Trim().ToLowerInvariant() switch
    {
        "9:16" or "vertical" => "vertical",
        "1:1" or "square" => "square",
        "16:9" or "horizontal" => "horizontal",
        _ => aspectRatio
    };

    private sealed record ZebracatVideoOperationData(string TaskId, string? Model);
}
