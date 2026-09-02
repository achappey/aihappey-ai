using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    private const string NinjaChatVideoEndpoint = "v1/videos";
    private const string NinjaChatVideoOperationPrefix = "ncv1_";

    private sealed record NinjaChatVideoOperation(string Id, string Model);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));
        if (request.Duration is < 4 or > 15)
            throw new ArgumentOutOfRangeException(nameof(request), "NinjaChat video duration must be between 4 and 15 seconds.");

        var warnings = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "NinjaChat creates one video per job." });

        var payload = BuildNinjaChatVideoPayload(request, warnings);
        ApplyAuthHeader();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, NinjaChatVideoEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, NinjaChatMediaJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureNinjaChatMediaSuccess(response, raw, "video submission");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var id = ReadNinjaChatString(root, "id")
            ?? throw new InvalidOperationException("NinjaChat video submission returned no id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeNinjaChatVideoOperation(id, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeNinjaChatVideoOperation(operation);
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{NinjaChatVideoEndpoint}/{Uri.EscapeDataString(operationData.Id)}");
        using var statusResponse = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        EnsureNinjaChatMediaSuccess(statusResponse, raw, "video status");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var status = ReadNinjaChatString(root, "status")?.Trim().ToLowerInvariant();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status == "failed")
            return new VideoOperationErrorResult
            {
                Error = ReadNinjaChatString(root, "error") ?? $"NinjaChat video job '{operationData.Id}' failed.",
                ProviderMetadata = metadata,
                Response = response
            };

        if (status is not "completed")
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };

        var videoUrl = ReadNinjaChatString(root, "video_url");
        if (string.IsNullOrWhiteSpace(videoUrl))
            return new VideoOperationErrorResult
            {
                Error = $"NinjaChat video job '{operationData.Id}' completed without a video_url.",
                ProviderMetadata = metadata,
                Response = response
            };

        var media = await DownloadNinjaChatMediaAsync(videoUrl, "video/mp4", cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos = [new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = media.MediaType,
                Data = Convert.ToBase64String(media.Bytes)
            }],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Dictionary<string, object?> BuildNinjaChatVideoPayload(VideoRequest request, List<object> warnings)
    {
        var payload = CopyNinjaChatProviderOptions(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["prompt"] = request.Prompt;
        payload["model"] = request.Model;
        if (request.Duration is not null)
            payload["duration"] = request.Duration.Value;
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;
        if (request.GenerateAudio is not null)
            payload["generate_audio"] = request.GenerateAudio.Value;

        var frames = request.FrameImages?.Where(static frame => frame?.Image is not null).ToList() ?? [];
        var firstFrame = frames.FirstOrDefault(static frame => IsNinjaChatFrame(frame.FrameType, "first"));
        var lastFrame = frames.FirstOrDefault(static frame => IsNinjaChatFrame(frame.FrameType, "last"));
        if (request.Image is not null)
            payload["image_url"] = ToNinjaChatVideoUri(request.Image);
        else if (firstFrame is not null)
            payload["image_url"] = ToNinjaChatVideoUri(firstFrame.Image);
        if (lastFrame is not null)
            payload["end_image_url"] = ToNinjaChatVideoUri(lastFrame.Image);

        var unknownFrames = frames.Where(static frame => !IsNinjaChatFrame(frame.FrameType, "first") && !IsNinjaChatFrame(frame.FrameType, "last")).ToList();
        if (unknownFrames.Count > 0)
            warnings.Add(new { type = "unsupported", feature = "frameImages", details = "Only first_frame and last_frame are supported." });
        if (frames.Count(static frame => IsNinjaChatFrame(frame.FrameType, "first")) > 1
            || frames.Count(static frame => IsNinjaChatFrame(frame.FrameType, "last")) > 1)
            warnings.Add(new { type = "unsupported", feature = "duplicate_frame_images", details = "Only the first image for each frame type was sent." });

        var references = request.InputReferences?.Where(static reference => reference is not null).Take(4).Select(ToNinjaChatVideoUri).ToArray() ?? [];
        if (references.Length > 0)
            payload["reference_images"] = references;
        if ((request.InputReferences?.Count() ?? 0) > 4)
            warnings.Add(new { type = "unsupported", feature = "inputReferences", details = "NinjaChat accepts at most four reference images; only the first four were sent." });

        return payload;
    }

    private static bool IsNinjaChatFrame(string? frameType, string expected)
        => string.Equals(frameType, expected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, $"{expected}_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, $"{expected}Frame", StringComparison.OrdinalIgnoreCase);

    private static string ToNinjaChatVideoUri(VideoFile file)
    {
        var data = file.Data?.Trim();
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException("NinjaChat video media data is required.", nameof(file));
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? MediaTypeNames.Image.Png : file.MediaType)};base64,{data}";
    }

    private static string EncodeNinjaChatVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new NinjaChatVideoOperation(id, model), NinjaChatMediaJson);
        return NinjaChatVideoOperationPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static NinjaChatVideoOperation DecodeNinjaChatVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !operation.StartsWith(NinjaChatVideoOperationPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A valid model-aware NinjaChat video operation token is required.", nameof(operation));

        try
        {
            var value = operation[NinjaChatVideoOperationPrefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            var result = JsonSerializer.Deserialize<NinjaChatVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(value)), NinjaChatMediaJson);
            if (result is null || string.IsNullOrWhiteSpace(result.Id) || string.IsNullOrWhiteSpace(result.Model))
                throw new JsonException();
            return result;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The NinjaChat video operation token is invalid.", nameof(operation), exception);
        }
    }

    private static Dictionary<string, object?> CopyNinjaChatProviderOptions(JsonElement options)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in options.EnumerateObject())
            result[property.Name] = property.Value.Clone();
        return result;
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadNinjaChatMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"NinjaChat media download failed ({(int)response.StatusCode}).");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static string? ReadNinjaChatString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void EnsureNinjaChatMediaSuccess(HttpResponseMessage response, string raw, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
            ? $"NinjaChat {operation} failed ({(int)response.StatusCode})."
            : $"NinjaChat {operation} failed ({(int)response.StatusCode}): {raw}");
    }
}
