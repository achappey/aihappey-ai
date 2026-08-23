using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Zenlayer;

public partial class ZenlayerProvider
{
    private const string VideoTokenPrefix = "zlv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        var family = ResolveVideoFamily(request.Model);
        var result = family switch
        {
            "vidu" => await StartViduAsync(request, cancellationToken),
            "seedance" => await StartSeedanceAsync(request, cancellationToken),
            "veo" => await StartVeoAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported Zenlayer video model '{request.Model}'.")
        };
        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(new VideoOperationData(family, result.Id, request.Model)),
            Warnings = VideoWarnings(request),
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
        if (string.IsNullOrWhiteSpace(operation)) throw new ArgumentException("A video operation is required.", nameof(operation));
        var data = DecodeVideoOperation(operation);
        return data.Family switch
        {
            "vidu" => await GetViduStatusAsync(data, cancellationToken),
            "seedance" => await GetSeedanceStatusAsync(data, cancellationToken),
            "veo" => await GetVeoStatusAsync(data, cancellationToken),
            _ => throw new ArgumentException("The Zenlayer video operation token contains an unsupported family.", nameof(operation))
        };
    }

    private async Task<VideoStartData> StartViduAsync(VideoRequest request, CancellationToken cancellationToken)
    {
        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(),
            "model", "prompt", "images", "duration", "seed", "resolution", "aspect_ratio", "audio");
        payload["model"] = request.Model;
        payload["prompt"] = request.Prompt;
        Set(payload, "duration", request.Duration);
        Set(payload, "seed", request.Seed);
        Set(payload, "resolution", request.Resolution);
        Set(payload, "aspect_ratio", request.AspectRatio);
        Set(payload, "audio", request.GenerateAudio);
        var frames = request.FrameImages?.ToList() ?? [];
        string endpoint;
        if (frames.Count >= 2)
        {
            endpoint = "v1/vidu/start-end2video";
            payload["images"] = new JsonArray(frames.OrderBy(frame => frame.FrameType.Contains("last", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .Select(frame => JsonValue.Create(NormalizeVideoMedia(frame.Image))).ToArray());
        }
        else if (request.Image is not null || frames.Count == 1)
        {
            endpoint = "v1/vidu/img2video";
            payload["images"] = new JsonArray(JsonValue.Create(NormalizeVideoMedia(request.Image ?? frames[0].Image)));
        }
        else
        {
            endpoint = "v1/vidu/text2video";
            if (!payload.ContainsKey("style")) payload["style"] = "general";
        }
        var response = await SendJsonAsync(HttpMethod.Post, endpoint, payload, "Vidu video creation", cancellationToken);
        var id = GetString(response.Root, "task_id") ?? throw new InvalidOperationException("Zenlayer Vidu returned no task_id.");
        return new VideoStartData(id, response.Root, response.Headers);
    }

    private async Task<VideoStartData> StartSeedanceAsync(VideoRequest request, CancellationToken cancellationToken)
    {
        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(),
            "model", "content", "resolution", "ratio", "duration", "seed", "generate_audio");
        payload["model"] = request.Model;
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = request.Prompt });
        foreach (var frame in request.FrameImages ?? [])
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = NormalizeVideoMedia(frame.Image) }, ["role"] = frame.FrameType });
        if (request.Image is not null)
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = NormalizeVideoMedia(request.Image) }, ["role"] = "first_frame" });
        foreach (var media in request.InputReferences ?? [])
        {
            var type = media.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? "video_url"
                : media.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? "audio_url" : "image_url";
            content.Add(new JsonObject { ["type"] = type, [type] = new JsonObject { ["url"] = NormalizeVideoMedia(media) }, ["role"] = "reference_" + type[..^4] });
        }
        payload["content"] = content;
        Set(payload, "resolution", request.Resolution);
        Set(payload, "ratio", request.AspectRatio);
        Set(payload, "duration", request.Duration);
        Set(payload, "seed", request.Seed);
        Set(payload, "generate_audio", request.GenerateAudio);
        var response = await SendJsonAsync(HttpMethod.Post, "v1/contents/generations/tasks", payload, "Seedance video creation", cancellationToken);
        var id = GetString(response.Root, "id") ?? throw new InvalidOperationException("Zenlayer Seedance returned no task id.");
        return new VideoStartData(id, response.Root, response.Headers);
    }

    private async Task<VideoStartData> StartVeoAsync(VideoRequest request, CancellationToken cancellationToken)
    {
        var payload = CreateVercelPayload(request.ProviderOptions, GetIdentifier(), "instances", "parameters", "model", "prompt");
        var instance = new JsonObject { ["prompt"] = request.Prompt };
        if (request.Image is not null) instance["image"] = ToVeoImage(request.Image);
        var frames = request.FrameImages?.ToList() ?? [];
        var last = frames.FirstOrDefault(frame => frame.FrameType.Contains("last", StringComparison.OrdinalIgnoreCase));
        if (last is not null) instance["lastFrame"] = ToVeoImage(last.Image);
        payload["instances"] = new JsonArray(instance);
        var parameters = payload["parameters"] as JsonObject ?? new JsonObject();
        Set(parameters, "durationSeconds", request.Duration);
        Set(parameters, "aspectRatio", request.AspectRatio);
        Set(parameters, "resolution", request.Resolution);
        payload["parameters"] = parameters;
        var endpoint = $"v1/v1beta/models/{Uri.EscapeDataString(request.Model)}:predictLongRunning";
        var response = await SendJsonAsync(HttpMethod.Post, endpoint, payload, "Veo video creation", cancellationToken, googleApiKey: true);
        var name = GetString(response.Root, "name") ?? throw new InvalidOperationException("Zenlayer Veo returned no operation name.");
        return new VideoStartData(name, response.Root, response.Headers);
    }

    private async Task<VideoOperationStatusResult> GetViduStatusAsync(VideoOperationData operation, CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync(HttpMethod.Get, $"v1/vidu/tasks/{Uri.EscapeDataString(operation.Id)}/creations", null, "Vidu video status", cancellationToken);
        var status = GetString(result.Root, "state")?.ToLowerInvariant();
        if (status is "failed" or "fail" or "error") return Error(operation, result, GetString(result.Root, "err_code") ?? "Vidu video generation failed.");
        if (status is not "success" and not "succeeded") return Pending(operation, result);
        var urls = new List<string>();
        if (result.Root.TryGetProperty("creations", out var creations) && creations.ValueKind == JsonValueKind.Array)
            urls.AddRange(creations.EnumerateArray().Select(item => GetString(item, "url")).Where(url => !string.IsNullOrWhiteSpace(url))!);
        return await Completed(operation, result, urls, cancellationToken);
    }

    private async Task<VideoOperationStatusResult> GetSeedanceStatusAsync(VideoOperationData operation, CancellationToken cancellationToken)
    {
        var result = await SendJsonAsync(HttpMethod.Get, $"v1/contents/generations/tasks/{Uri.EscapeDataString(operation.Id)}", null, "Seedance video status", cancellationToken);
        var status = GetString(result.Root, "status")?.ToLowerInvariant();
        if (status is "failed" or "cancelled" or "expired") return Error(operation, result, GetString(result.Root, "error", "message") ?? $"Seedance task ended with status '{status}'.");
        if (status != "succeeded") return Pending(operation, result);
        return await Completed(operation, result, [GetString(result.Root, "content", "video_url")], cancellationToken);
    }

    private async Task<VideoOperationStatusResult> GetVeoStatusAsync(VideoOperationData operation, CancellationToken cancellationToken)
    {
        var endpoint = "v1/v1beta/" + operation.Id.TrimStart('/');
        var result = await SendJsonAsync(HttpMethod.Get, endpoint, null, "Veo video status", cancellationToken, googleApiKey: true);
        if (result.Root.TryGetProperty("error", out var error)) return Error(operation, result, GetString(error, "message") ?? "Veo video generation failed.");
        if (!result.Root.TryGetProperty("done", out var done) || !done.GetBoolean()) return Pending(operation, result);
        var urls = new List<string?>();
        if (result.Root.TryGetProperty("response", out var response)
            && response.TryGetProperty("generateVideoResponse", out var videoResponse)
            && videoResponse.TryGetProperty("generatedSamples", out var samples) && samples.ValueKind == JsonValueKind.Array)
            urls.AddRange(samples.EnumerateArray().Select(sample => GetString(sample, "video", "uri")));
        return await Completed(operation, result, urls, cancellationToken, googleApiKey: true);
    }

    private VideoOperationPendingResult Pending(VideoOperationData operation, ZenlayerJsonResult result) => new()
    {
        ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root), Response = Header(operation, result)
    };
    private VideoOperationErrorResult Error(VideoOperationData operation, ZenlayerJsonResult result, string error) => new()
    {
        Error = error, ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root), Response = Header(operation, result)
    };
    private async Task<VideoOperationStatusResult> Completed(
        VideoOperationData operation, ZenlayerJsonResult result, IEnumerable<string?> urls, CancellationToken cancellationToken, bool googleApiKey = false)
    {
        var videos = new List<VideoOperationVideoData>();
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)))
        {
            var media = await DownloadAsync(url!, cancellationToken, googleApiKey);
            videos.Add(new VideoOperationVideoData { Type = "base64", MediaType = media.MediaType.StartsWith("video/") ? media.MediaType : "video/mp4", Data = Convert.ToBase64String(media.Bytes) });
        }
        if (videos.Count == 0) return Error(operation, result, $"Zenlayer {operation.Family} task completed without a video URL.");
        return new VideoOperationCompletedResult
        {
            Videos = videos, Warnings = [], ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(result.Root), Response = Header(operation, result)
        };
    }
    private HeaderResponseData Header(VideoOperationData operation, ZenlayerJsonResult result) => new()
    {
        Timestamp = DateTime.UtcNow, Headers = result.Headers, ModelId = operation.Model.ToModelId(GetIdentifier())
    };

    private static string EncodeVideoOperation(VideoOperationData operation)
    {
        var json = JsonSerializer.Serialize(operation, MediaJson);
        return VideoTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static VideoOperationData DecodeVideoOperation(string operation)
    {
        if (!operation.StartsWith(VideoTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Legacy Zenlayer video operation IDs do not contain the model required by status routes. Start a new operation.", nameof(operation));
        try
        {
            var raw = operation[VideoTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight(raw.Length + ((4 - raw.Length % 4) % 4), '=');
            var value = JsonSerializer.Deserialize<VideoOperationData>(Encoding.UTF8.GetString(Convert.FromBase64String(raw)), MediaJson);
            if (value is null || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Model) || string.IsNullOrWhiteSpace(value.Family)) throw new JsonException();
            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Zenlayer video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static string ResolveVideoFamily(string model) => model.StartsWith("vidu", StringComparison.OrdinalIgnoreCase) ? "vidu"
        : model.Contains("seedance", StringComparison.OrdinalIgnoreCase) ? "seedance"
        : model.StartsWith("veo", StringComparison.OrdinalIgnoreCase) ? "veo" : string.Empty;
    private static string NormalizeVideoMedia(VideoFile media) => media.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || media.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        ? media.Data : $"data:{media.MediaType};base64,{media.Data}";
    private static JsonObject ToVeoImage(VideoFile media)
    {
        var data = media.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? media.Data[(media.Data.IndexOf(',') + 1)..] : media.Data;
        return new JsonObject { ["bytesBase64Encoded"] = data, ["mimeType"] = media.MediaType };
    }
    private static IEnumerable<object> VideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        return warnings;
    }
    private sealed record VideoOperationData(string Family, string Id, string Model);
    private sealed record VideoStartData(string Id, JsonElement Root, Dictionary<string, string> Headers);
}
