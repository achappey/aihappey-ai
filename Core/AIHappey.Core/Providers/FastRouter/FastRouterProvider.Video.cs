using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FastRouter;

public partial class FastRouterProvider
{
    private const string FastRouterVideoOperationPrefix = "frv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));

        var payload = CreateFastRouterPayload(request.ProviderOptions,
            "model", "prompt", "image", "length", "duration", "seconds", "resolution", "aspectRatio",
            "aspect_ratio", "size", "mode", "seed", "generateAudio", "generate_audio", "n", "fps");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        if (request.Duration is not null) payload["length"] = request.Duration.Value;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspectRatio"] = request.AspectRatio;
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        if (request.GenerateAudio is not null) payload["generateAudio"] = request.GenerateAudio.Value;

        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.InputReferences?.Any() == true) warnings.Add(new { type = "unsupported", feature = "inputReferences" });
        if (request.FrameImages?.Any() == true) warnings.Add(new { type = "unsupported", feature = "frameImages" });

        if (request.Image is not null)
        {
            var image = request.Image.Data;
            if (!image.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "unsupported", feature = "base64 image; FastRouter videos require an image URL" });
            }
            else payload["image"] = image;
        }

        var result = await SendFastRouterJsonAsync(HttpMethod.Post, "v1/videos", payload, "video generation", cancellationToken);
        var taskId = GetFastRouterString(result.Root, "data", "taskId")
            ?? GetFastRouterString(result.Root, "taskId")
            ?? GetFastRouterString(result.Root, "id");
        if (string.IsNullOrWhiteSpace(taskId))
            throw new InvalidOperationException("FastRouter video generation did not return a task ID.");

        return new VideoOperationStartResult
        {
            Operation = EncodeFastRouterVideoOperation(taskId, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = result.Headers,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeFastRouterVideoOperation(operation);
        var payload = new JsonObject { ["taskId"] = operationData.TaskId };
        if (!string.IsNullOrWhiteSpace(operationData.Model)) payload["model"] = operationData.Model;
        var result = await SendFastRouterJsonAsync(HttpMethod.Post, "v1/getAsyncResponse", payload, "video status", cancellationToken);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = result.Headers,
            ModelId = string.IsNullOrWhiteSpace(operationData.Model)
                ? GetIdentifier()
                : operationData.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root);
        var status = ResolveFastRouterVideoStatus(result.Root);

        if (status is "failed" or "error" or "cancelled")
            return new VideoOperationErrorResult
            {
                Error = GetFastRouterString(result.Root, "message")
                    ?? GetFastRouterString(result.Root, "error")
                    ?? $"FastRouter video task '{operationData.TaskId}' failed.",
                ProviderMetadata = metadata,
                Response = response
            };

        if (status is not ("succeed" or "succeeded" or "completed"))
            return new VideoOperationPendingResult { ProviderMetadata = metadata, Response = response };

        var videos = await ReadFastRouterVideosAsync(result.Root, cancellationToken);
        if (videos.Count == 0)
            return new VideoOperationErrorResult
            {
                Error = $"FastRouter video task '{operationData.TaskId}' completed without a downloadable video.",
                ProviderMetadata = metadata,
                Response = response
            };

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private async Task<List<VideoOperationVideoData>> ReadFastRouterVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var videos = new List<VideoOperationVideoData>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("generations", out var generations) && generations.ValueKind == JsonValueKind.Array)
        {
            foreach (var generation in generations.EnumerateArray())
            {
                var base64 = GetFastRouterString(generation, "bytesBase64Encoded");
                if (!string.IsNullOrWhiteSpace(base64))
                    videos.Add(new VideoOperationVideoData { Type = "base64", MediaType = "video/mp4", Data = base64 });
                else
                    await AddFastRouterVideoUrlAsync(videos, GetFastRouterString(generation, "url"), cancellationToken);
            }
        }

        if (videos.Count == 0 && root.TryGetProperty("fastrouter_assets", out var assets)
            && assets.TryGetProperty("urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var url in urls.EnumerateArray())
                if (url.ValueKind == JsonValueKind.String)
                    await AddFastRouterVideoUrlAsync(videos, url.GetString(), cancellationToken);
        }
        return videos;
    }

    private async Task AddFastRouterVideoUrlAsync(List<VideoOperationVideoData> videos, string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        using var download = await _client.GetAsync(uri, cancellationToken);
        var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!download.IsSuccessStatusCode)
            throw new InvalidOperationException($"FastRouter video download failed ({(int)download.StatusCode}).");
        videos.Add(new VideoOperationVideoData
        {
            Type = "base64",
            MediaType = download.Content.Headers.ContentType?.MediaType ?? ResolveFastRouterVideoMimeType(url),
            Data = Convert.ToBase64String(bytes)
        });
    }

    private static string ResolveFastRouterVideoStatus(JsonElement root)
    {
        var status = GetFastRouterString(root, "data", "status") ?? GetFastRouterString(root, "status");
        if (string.IsNullOrWhiteSpace(status) && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("generations", out var generations)
            && generations.ValueKind == JsonValueKind.Array)
        {
            var statuses = generations.EnumerateArray().Select(item => GetFastRouterString(item, "status")).Where(value => value is not null).ToList();
            if (statuses.Any(value => value!.Equals("failed", StringComparison.OrdinalIgnoreCase))) status = "failed";
            else if (statuses.Count > 0 && statuses.All(value => value is "succeed" or "completed")) status = "completed";
            else if (statuses.Count > 0) status = "processing";
        }
        return status?.Trim().ToLowerInvariant() ?? "processing";
    }

    private static string EncodeFastRouterVideoOperation(string taskId, string model)
    {
        var json = JsonSerializer.Serialize(new { taskId, model }, FastRouterJsonOptions);
        return FastRouterVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (string TaskId, string? Model) DecodeFastRouterVideoOperation(string operation)
    {
        if (!operation.StartsWith(FastRouterVideoOperationPrefix, StringComparison.Ordinal))
            return (Uri.UnescapeDataString(operation), null);

        try
        {
            var encoded = operation[FastRouterVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            var taskId = GetFastRouterString(document.RootElement, "taskId");
            var model = GetFastRouterString(document.RootElement, "model");
            if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(model)) throw new JsonException();
            return (taskId, model);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The FastRouter video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string ResolveFastRouterVideoMimeType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm" : "video/mp4";
}
