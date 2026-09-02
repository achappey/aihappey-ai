using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.NanoGPT;

public partial class NanoGPTProvider
{
    private const string NanoGPTVideoOperationPrefix = "ngv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        var payload = CopyNanoGPTOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
        if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
        if (request.Duration is not null) payload["duration"] = request.Duration.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (request.Seed is not null) payload["seed"] = request.Seed.Value;
        if (request.Fps is not null) payload["frames_per_second"] = request.Fps.Value;
        if (request.GenerateAudio is not null) payload["generateAudio"] = request.GenerateAudio.Value;
        var references = request.InputReferences?.ToArray() ?? [];
        var image = request.Image ?? references.FirstOrDefault();
        if (image is not null)
        {
            var value = NanoGPTVideoMediaValue(image);
            payload[value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "imageUrl" : "imageDataUrl"] = value;
        }
        if (references.Length > (request.Image is null ? 1 : 0))
            payload["referenceImages"] = references.Skip(request.Image is null ? 1 : 0).Select(NanoGPTVideoMediaValue).ToArray();
        var frames = request.FrameImages?.ToArray() ?? [];
        foreach (var frame in frames)
        {
            var key = string.Equals(frame.FrameType, "last_frame", StringComparison.OrdinalIgnoreCase) ? "lastFrameImage" : "firstFrameImage";
            payload[key] = NanoGPTVideoMediaValue(frame.Image);
        }
        payload.Remove("stream");
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "generate-video")
        { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json) };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureNanoGPTSuccess(response, raw, "video submission");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = NanoGPTGetString(root, "runId", "id") ?? throw new InvalidOperationException("NanoGPT video submission returned no runId.");
        return new VideoOperationStartResult
        {
            Operation = EncodeNanoGPTVideoOperation(id, request.Model), Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(NanoGPTCreated(root)).UtcDateTime,
                Headers = response.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        var data = DecodeNanoGPTVideoOperation(operation);
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"video/status?requestId={Uri.EscapeDataString(data.Id)}");
        using var statusResponse = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureNanoGPTSuccess(statusResponse, raw, "video status");
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var response = new HeaderResponseData
        {
            Timestamp = DateTimeOffset.FromUnixTimeSeconds(NanoGPTCreated(root)).UtcDateTime,
            Headers = statusResponse.GetHeaders(), ModelId = data.Model.ToModelId(GetIdentifier())
        };
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var statusRoot = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        var status = NanoGPTGetString(statusRoot, "status")?.Trim().ToUpperInvariant();
        if (status is "FAILED" or "CANCELED" or "CANCELLED" or "ERROR")
            return new VideoOperationErrorResult
            {
                Error = ReadNanoGPTVideoError(statusRoot) ?? $"NanoGPT video job '{data.Id}' failed.",
                ProviderMetadata = metadata, Response = response
            };
        if (status is not "COMPLETED" and not "COMPLETE" and not "SUCCEEDED" and not "SUCCESS" and not "DONE")
            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };
        var url = FindNanoGPTVideoUrl(statusRoot);
        if (string.IsNullOrWhiteSpace(url))
            return new VideoOperationErrorResult
            {
                Error = $"NanoGPT video job '{data.Id}' completed without a video URL.",
                ProviderMetadata = metadata, Response = response
            };
        var media = await DownloadNanoGPTMediaAsync(url, true, cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData { Type = "base64", MediaType = media.MediaType, Data = media.Base64 }],
            Warnings = [], ProviderMetadata = metadata, Response = response
        };
    }

    private static string? FindNanoGPTVideoUrl(JsonElement root)
    {
        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
        {
            if (output.TryGetProperty("video", out var video))
            {
                if (video.ValueKind == JsonValueKind.String) return video.GetString();
                if (video.ValueKind == JsonValueKind.Object) return NanoGPTGetString(video, "url", "videoUrl", "video_url");
            }
            var direct = NanoGPTGetString(output, "url", "videoUrl", "video_url");
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }
        return NanoGPTGetString(root, "videoUrl", "video_url", "url");
    }

    private static string? ReadNanoGPTVideoError(JsonElement root)
    {
        var direct = NanoGPTGetString(root, "error", "userFriendlyError", "message");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            return NanoGPTGetString(error, "message", "detail", "code") ?? error.GetRawText();
        return null;
    }

    private static string EncodeNanoGPTVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new NanoGPTVideoOperation(id, model));
        return NanoGPTVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static NanoGPTVideoOperation DecodeNanoGPTVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(NanoGPTVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A valid model-aware NanoGPT video operation token is required.", nameof(operation));
        try
        {
            var value = operation[NanoGPTVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<NanoGPTVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            if (result is null || string.IsNullOrWhiteSpace(result.Id) || string.IsNullOrWhiteSpace(result.Model)) throw new JsonException();
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new ArgumentException("The NanoGPT video operation token is invalid.", nameof(operation), exception); }
    }

    private static string NanoGPTVideoMediaValue(VideoFile media)
        => media.Data.StartsWith("http", StringComparison.OrdinalIgnoreCase) || media.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? media.Data : $"data:{media.MediaType};base64,{media.Data}";
    private sealed record NanoGPTVideoOperation(string Id, string Model);
}
