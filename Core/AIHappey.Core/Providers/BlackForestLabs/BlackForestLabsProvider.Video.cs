using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.BlackForestLabs;

public partial class BlackForestLabsProvider
{
    private const string BflVideoModel = "flux-3-video";
    private const string BflDraftEnhanceVideoModel = "flux-3-video-draft-enhance";
    private const string BflVideoOperationTokenPrefix = "bflv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var model = NormalizeBflVideoModel(request.Model);
        if (model is not BflVideoModel and not BflDraftEnhanceVideoModel)
            throw new NotSupportedException($"BlackForestLabs video model '{request.Model}' is not supported.");

        ApplyAuthHeader();
        var warnings = BuildBflVideoWarnings(request, model);
        var payload = BuildBflVideoPayload(request, model);
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "v1/flux-3-video")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"BlackForestLabs FLUX 3 video submit failed ({(int)submitResponse.StatusCode}): {submitRaw}");

        using var submitDocument = JsonDocument.Parse(submitRaw);
        var submitRoot = submitDocument.RootElement.Clone();
        var taskId = submitRoot.TryGetString("id")
            ?? throw new InvalidOperationException("BlackForestLabs FLUX 3 video response missing id.");
        var pollingUri = ResolvePollingUri(submitRoot, taskId);

        return new VideoOperationStartResult
        {
            Operation = EncodeBflVideoOperation(taskId, request.Model, pollingUri),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(submitRoot),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = submitResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeBflVideoOperation(operation);
        ApplyAuthHeader();

        var pollResult = await PollResultAsync(
            operationData.TaskId,
            new Uri(operationData.PollingUrl, UriKind.Absolute),
            cancellationToken);
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(pollResult.Root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (string.Equals(pollResult.Status, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            var sample = TryGetBflVideoSample(pollResult.Root);
            if (string.IsNullOrWhiteSpace(sample))
            {
                return new VideoOperationErrorResult
                {
                    Error = $"BlackForestLabs FLUX 3 video task '{operationData.TaskId}' completed without result.sample.",
                    ProviderMetadata = metadata,
                    Response = response
                };
            }

            var video = await GetBflVideoDataAsync(sample, cancellationToken);
            return new VideoOperationCompletedResult
            {
                Videos = [video],
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (IsBflVideoErrorStatus(pollResult.Status))
        {
            return new VideoOperationErrorResult
            {
                Error = $"BlackForestLabs FLUX 3 video task '{operationData.TaskId}' failed with status '{pollResult.Status}'.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationPendingResult
        {
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildBflVideoPayload(VideoRequest request, string model)
    {
        var draftEnhance = model == BflDraftEnhanceVideoModel;
        var references = request.InputReferences?.ToList() ?? [];
        var hasImages = request.Image is not null || request.FrameImages?.Any() == true;
        var mode = draftEnhance ? "draft_enhance" : references.Count > 0 ? "v2v" : hasImages ? "i2v" : "t2v";
        var payload = new Dictionary<string, object?>();

        if (!draftEnhance)
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
                throw new ArgumentException("Prompt is required for FLUX 3 video generation.", nameof(request));

            payload["prompt"] = request.Prompt;
            if (!string.IsNullOrWhiteSpace(request.AspectRatio)) payload["aspect_ratio"] = request.AspectRatio;
            if (request.Duration is not null) payload["duration"] = request.Duration;
            if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
            if (request.GenerateAudio is not null) payload["generate_audio"] = request.GenerateAudio;

            if (mode == "v2v")
                payload["start_video"] = NormalizeBflVideoInput(references[0], "inputReferences[0]");
            else if (mode == "i2v")
                payload["keyframes"] = BuildBflVideoKeyframes(request);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(request.Resolution)) payload["resolution"] = request.Resolution;
            if (references.Count > 0)
                payload["draft_cache"] = NormalizeBflVideoInput(references[0], "inputReferences[0]");
        }

        MergeBflVideoProviderOptions(payload, request);
        payload["mode"] = mode;

        if (draftEnhance && (!payload.TryGetValue("draft_cache", out var cache) || !HasBflValue(cache)))
            throw new ArgumentException(
                "Draft enhance requires inputReferences[0].data or providerOptions.blackforestlabs.draft_cache.",
                nameof(request));

        return payload;
    }

    private static List<string> BuildBflVideoKeyframes(VideoRequest request)
    {
        var frames = request.FrameImages?.ToList() ?? [];
        if (frames.Count > 10)
            throw new ArgumentException("FLUX 3 video supports at most 10 keyframes.", nameof(request));

        VideoFile? first = request.Image;
        VideoFile? last = null;
        var middle = new List<VideoFile>();
        foreach (var frame in frames)
        {
            if (frame?.Image is null)
                throw new ArgumentException("Every frameImages entry must include an image.", nameof(request));

            if (IsFirstBflFrame(frame.FrameType)) first = frame.Image;
            else if (IsLastBflFrame(frame.FrameType)) last = frame.Image;
            else middle.Add(frame.Image);
        }

        var ordered = new List<VideoFile>();
        if (first is not null) ordered.Add(first);
        ordered.AddRange(middle);
        if (last is not null) ordered.Add(last);
        if (ordered.Count is < 1 or > 10)
            throw new ArgumentException("FLUX 3 image-to-video requires between 1 and 10 keyframes.", nameof(request));

        return ordered.Select((frame, index) => NormalizeBflVideoInput(frame, $"keyframes[{index}]")).ToList();
    }

    private static void MergeBflVideoProviderOptions(Dictionary<string, object?> payload, VideoRequest request)
    {
        var options = request.GetProviderMetadata<JsonElement>("blackforestlabs");
        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (property.NameEquals("mode") || property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static List<object> BuildBflVideoWarnings(VideoRequest request, string model)
    {
        List<object> warnings = [];
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is > 1) warnings.Add(new { type = "unsupported", feature = "n" });
        if (request.Seed is not null) warnings.Add(new { type = "unsupported", feature = "seed" });

        if (model == BflDraftEnhanceVideoModel)
        {
            if (!string.IsNullOrWhiteSpace(request.Prompt)) warnings.Add(new { type = "unsupported", feature = "prompt" });
            if (!string.IsNullOrWhiteSpace(request.AspectRatio)) warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });
            if (request.Duration is not null) warnings.Add(new { type = "unsupported", feature = "duration" });
            if (request.GenerateAudio is not null) warnings.Add(new { type = "unsupported", feature = "generate_audio" });
            if (request.Image is not null || request.FrameImages?.Any() == true)
                warnings.Add(new { type = "unsupported", feature = "image inputs" });
        }
        else if (request.InputReferences?.Skip(1).Any() == true)
        {
            warnings.Add(new { type = "unsupported", feature = "inputReferences", details = "Only the first video reference is used." });
        }

        return warnings;
    }

    private static string NormalizeBflVideoInput(VideoFile input, string name)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Data))
            throw new ArgumentException($"{name}.data is required.", name);

        var value = input.Data.Trim();
        return value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? value.RemoveDataUrlPrefix()
            : value;
    }

    private async Task<VideoOperationVideoData> GetBflVideoDataAsync(string sample, CancellationToken cancellationToken)
    {
        if (sample.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = sample.IndexOf(',');
            var header = separator >= 0 ? sample[..separator] : string.Empty;
            var mediaType = header.Length > 5 ? header[5..].Split(';')[0] : "video/mp4";
            return new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = string.IsNullOrWhiteSpace(mediaType) ? "video/mp4" : mediaType,
                Data = sample.RemoveDataUrlPrefix()
            };
        }

        if (Uri.TryCreate(sample, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var downloadResponse = await _client.GetAsync(uri, cancellationToken);
            var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!downloadResponse.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"BlackForestLabs FLUX 3 video download failed ({(int)downloadResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            return new VideoOperationVideoData
            {
                Type = "base64",
                MediaType = downloadResponse.Content.Headers.ContentType?.MediaType ?? GuessBflVideoMediaType(sample),
                Data = Convert.ToBase64String(bytes)
            };
        }

        return new VideoOperationVideoData { Type = "base64", MediaType = "video/mp4", Data = sample };
    }

    private static string? TryGetBflVideoSample(JsonElement root)
        => root.TryGetProperty("result", out var result)
           && result.ValueKind == JsonValueKind.Object
           && result.TryGetProperty("sample", out var sample)
           && sample.ValueKind == JsonValueKind.String
            ? sample.GetString()
            : null;

    private static bool IsBflVideoErrorStatus(string? status)
        => status is not null && (status.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Request Moderated", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Content Moderated", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Task not found", StringComparison.OrdinalIgnoreCase));

    private static bool IsFirstBflFrame(string? frameType)
        => string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase)
           || string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
           || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase);

    private static bool IsLastBflFrame(string? frameType)
        => string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase)
           || string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
           || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase);

    private static bool HasBflValue(object? value)
        => value switch
        {
            string text => !string.IsNullOrWhiteSpace(text),
            JsonElement element when element.ValueKind == JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            _ => value is not null
        };

    private static string NormalizeBflVideoModel(string model)
    {
        var normalized = model.Trim();
        const string prefix = "blackforestlabs/";
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[prefix.Length..];
        return normalized.ToLowerInvariant();
    }

    private static string GuessBflVideoMediaType(string url)
        => url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? "video/webm"
            : url.Contains(".mov", StringComparison.OrdinalIgnoreCase) ? "video/quicktime"
            : "video/mp4";

    private static string EncodeBflVideoOperation(string taskId, string model, Uri pollingUri)
    {
        var json = JsonSerializer.Serialize(new BflVideoOperationData(taskId, model, pollingUri.AbsoluteUri), JsonOptions);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return BflVideoOperationTokenPrefix + encoded;
    }

    private static BflVideoOperationData DecodeBflVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(BflVideoOperationTokenPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("A model-aware BlackForestLabs video operation token is required.", nameof(operation));
        }

        try
        {
            var encoded = operation[BflVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var data = JsonSerializer.Deserialize<BflVideoOperationData>(json, JsonOptions);
            if (data is null
                || string.IsNullOrWhiteSpace(data.TaskId)
                || string.IsNullOrWhiteSpace(data.Model)
                || !Uri.TryCreate(data.PollingUrl, UriKind.Absolute, out var pollingUri)
                || pollingUri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("The BlackForestLabs video operation token is invalid.", nameof(operation));
            }

            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
            throw new ArgumentException("The BlackForestLabs video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record BflVideoOperationData(string TaskId, string Model, string PollingUrl);
}
