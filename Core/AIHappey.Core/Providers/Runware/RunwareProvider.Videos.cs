using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private const string RunwareVideoOperationTokenPrefix = "rwav1_";

    private static readonly JsonSerializerOptions RunwareVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };


    public async Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) && request.Image is null)
            throw new ArgumentException("Prompt or image/video is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspect_ratio" });

        var metadata = GetVideoProviderMetadata<RunwareProviderMetadata>(request, GetIdentifier());

        var taskUuid = Guid.NewGuid().ToString();
        var payload = BuildVideoInferencePayload(request, metadata, taskUuid, warnings);

        var json = JsonSerializer.Serialize(new[] { payload }, RunwareVideoJson);
        using var createResp = await _client.PostAsync(
            "",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runware video inference failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var createError = TryGetError(createRoot);
        if (!string.IsNullOrWhiteSpace(createError))
            throw new InvalidOperationException($"Runware video inference failed: {createError}");

        var resolvedTaskUuid = TryGetTaskUuid(createRoot) ?? taskUuid;
        return new VideoOperationStartResult
        {
            Operation = EncodeRunwareVideoOperation(resolvedTaskUuid, request.Model),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(createRoot),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeRunwareVideoOperation(operation);
        ApplyAuthHeader();

        var pollPayload = new Dictionary<string, object?>
        {
            ["taskType"] = "getResponse",
            ["taskUUID"] = operationData.TaskUuid
        };
        var pollJson = JsonSerializer.Serialize(new[] { pollPayload }, RunwareVideoJson);
        using var pollResp = await _client.PostAsync(
            "",
            new StringContent(pollJson, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runware getResponse failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var error = TryGetError(root);
        if (!string.IsNullOrWhiteSpace(error))
            return new VideoOperationErrorResult { Error = error, ProviderMetadata = metadata, Response = response };

        var status = TryGetStatus(root);
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                return new VideoOperationErrorResult
                {
                    Error = $"Runware video inference failed with status '{status}' (taskUUID={operationData.TaskUuid}).",
                    ProviderMetadata = metadata,
                    Response = response
                };
            }

            return new VideoOperationPendingResult { Warnings = [], ProviderMetadata = metadata, Response = response };
        }

        var videos = await ResolveVideoOutputsAsync(root, cancellationToken);
        if (videos.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"Runware task '{operationData.TaskUuid}' succeeded but returned no video output.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildVideoInferencePayload(
        VideoRequest request,
        RunwareProviderMetadata? metadata,
        string taskUuid,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["taskType"] = "videoInference",
            ["taskUUID"] = taskUuid,
            ["model"] = request.Model,
            ["positivePrompt"] = request.Prompt
        };

        payload["deliveryMethod"] = "async";
        AddIfNotNull(payload, "outputType", metadata?.OutputType);
        AddIfNotNull(payload, "outputFormat", metadata?.OutputFormat);
        AddIfNotNull(payload, "outputQuality", metadata?.OutputQuality);
        AddIfNotNull(payload, "webhookURL", metadata?.WebhookUrl);
        AddIfNotNull(payload, "uploadEndpoint", metadata?.UploadEndpoint);
        AddIfNotNull(payload, "ttl", metadata?.Ttl);
        AddIfNotNull(payload, "includeCost", metadata?.IncludeCost);
        AddIfNotNull(payload, "negativePrompt", string.IsNullOrWhiteSpace(metadata?.NegativePrompt) ? null : metadata!.NegativePrompt);
        AddIfNotNull(payload, "safety", metadata?.Safety);
        AddIfNotNull(payload, "steps", metadata?.Steps);
        AddIfNotNull(payload, "CFGScale", metadata?.CFGScale);
        AddIfNotNull(payload, "acceleration", metadata?.Acceleration);
        AddIfNotNull(payload, "advancedFeatures", metadata?.AdvancedFeatures);
        AddIfNotNull(payload, "providerSettings", metadata?.ProviderSettings);

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.Fps is not null)
            payload["fps"] = request.Fps.Value;
        else if (metadata?.Fps is not null)
            payload["fps"] = metadata.Fps.Value;

        if (request.N is not null)
            payload["numberResults"] = request.N.Value;
        else if (metadata?.NumberResults is not null)
            payload["numberResults"] = metadata.NumberResults.Value;

        if (request.Seed is not null)
            payload["seed"] = (long)request.Seed.Value;
        else if (metadata?.Seed is not null)
            payload["seed"] = metadata.Seed.Value;

        if (!string.IsNullOrWhiteSpace(request.Resolution)
            && TryParseSize(request.Resolution) is { } wh)
        {
            payload["width"] = wh.width;
            payload["height"] = wh.height;
        }

        if (request.Image is not null)
        {
            ValidateBase64Only(request.Image.Data, "image");

            if (request.Image.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                payload["frameImages"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["inputImages"] = request.Image.Data,
                        ["frame"] = "first"
                    }
                };
            }
            else if (request.Image.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                payload["referenceVideos"] = new[] { request.Image.Data };
            }
            else
            {
                throw new ArgumentException($"Unsupported mediaType '{request.Image.MediaType}'. Expected image/* or video/*.", nameof(request));
            }
        }

        if (metadata?.FrameImages is not null)
        {
            foreach (var frame in metadata.FrameImages)
            {
                if (string.IsNullOrWhiteSpace(frame.InputImages))
                    continue;

                ValidateBase64Only(frame.InputImages, "frameImages.inputImages");
            }

            payload["frameImages"] = metadata.FrameImages;
        }

        if (metadata?.ReferenceImages is not null)
        {
            foreach (var img in metadata.ReferenceImages)
                ValidateBase64Only(img, "referenceImages");

            payload["referenceImages"] = metadata.ReferenceImages;
        }

        if (metadata?.ReferenceVideos is not null)
        {
            foreach (var vid in metadata.ReferenceVideos)
                ValidateBase64Only(vid, "referenceVideos");

            payload["referenceVideos"] = metadata.ReferenceVideos;
        }

        if (metadata?.InputAudios is not null)
        {
            foreach (var audio in metadata.InputAudios)
                ValidateBase64Only(audio, "inputAudios");

            payload["inputAudios"] = metadata.InputAudios;
        }

        if (metadata?.Speech is not null)
            payload["speech"] = metadata.Speech;

        return payload;
    }

    private static void ValidateBase64Only(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Runware video generation only supports raw base64 for {fieldName} (data URI not allowed).");

        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Runware video generation only supports raw base64 for {fieldName} (URL not allowed).");
    }

    private async Task<List<VideoOperationVideoData>> ResolveVideoOutputsAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        List<VideoOperationVideoData> videos = [];
        if (!root.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var data in dataArray.EnumerateArray().Where(item =>
                     item.ValueKind == JsonValueKind.Object
                     && (!item.TryGetProperty("status", out var status)
                         || string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase))))
        {
            if (data.TryGetProperty("videoURL", out var urlEl) && urlEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(urlEl.GetString()))
            {
                var url = urlEl.GetString()!;
                var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
                videos.Add(new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = Convert.ToBase64String(bytes),
                    MediaType = GuessVideoMediaType(url) ?? "video/mp4"
                });
            }
            else if (data.TryGetProperty("videoDataURI", out var dataUriEl) && dataUriEl.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(dataUriEl.GetString()))
            {
                var dataUri = dataUriEl.GetString()!;
                videos.Add(new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = ExtractBase64FromDataUri(dataUri),
                    MediaType = TryGetDataUriMediaType(dataUri) ?? "video/mp4"
                });
            }
            else if (data.TryGetProperty("videoBase64Data", out var b64El) && b64El.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(b64El.GetString()))
            {
                videos.Add(new VideoOperationVideoData
                {
                    Type = "base64",
                    Data = b64El.GetString(),
                    MediaType = "video/mp4"
                });
            }
        }

        return videos;
    }

    private static JsonElement TryGetFirstDataElement(JsonElement root)
    {
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
        {
            var first = dataEl.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined)
                return first;
        }

        return default;
    }

    private static string? TryGetStatus(JsonElement root)
    {
        var data = TryGetPreferredDataElement(root);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;
    }

    private static string? TryGetError(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var first = errors.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
                return TryGetErrorMessage(first);
        }

        var data = TryGetPreferredDataElement(root);
        return data.ValueKind == JsonValueKind.Object ? TryGetErrorMessage(data) : null;
    }

    private static JsonElement TryGetPreferredDataElement(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return default;

        var values = data.EnumerateArray().ToArray();
        return values.FirstOrDefault(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("status", out var status)
                && (string.Equals(status.GetString(), "success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status.GetString(), "failed", StringComparison.OrdinalIgnoreCase)))
            is var terminal && terminal.ValueKind != JsonValueKind.Undefined
                ? terminal
                : values.FirstOrDefault();
    }

    private static string? TryGetErrorMessage(JsonElement value)
    {
        foreach (var propertyName in new[] { "message", "error", "code" })
        {
            if (value.TryGetProperty(propertyName, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                    return property.GetString();
                if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("message", out var message))
                    return message.GetString();
            }
        }

        return null;
    }

    private static string? TryGetTaskUuid(JsonElement root)
    {
        var data = TryGetFirstDataElement(root);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("taskUUID", out var taskEl)
            && taskEl.ValueKind == JsonValueKind.String
                ? taskEl.GetString()
                : null;
    }

    private static string EncodeRunwareVideoOperation(string taskUuid, string model)
    {
        var json = JsonSerializer.Serialize(new RunwareVideoOperationData(taskUuid, model), RunwareVideoJson);
        return RunwareVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static RunwareVideoOperationData DecodeRunwareVideoOperation(string operation)
    {
        if (!operation.StartsWith(RunwareVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware Runware video operation token is required.", nameof(operation));

        var encoded = operation[RunwareVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var data = JsonSerializer.Deserialize<RunwareVideoOperationData>(json, RunwareVideoJson);
            if (data is null || string.IsNullOrWhiteSpace(data.TaskUuid) || string.IsNullOrWhiteSpace(data.Model))
                throw new ArgumentException("The Runware video operation token is invalid.", nameof(operation));
            return data;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The Runware video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record RunwareVideoOperationData(string TaskUuid, string Model);

    private static string? OutputFormatToVideoMime(string? outputFormat)
        => outputFormat?.Trim().ToUpperInvariant() switch
        {
            "WEBM" => "video/webm",
            "MOV" => "video/quicktime",
            "MP4" => "video/mp4",
            _ => null
        };

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }

    private static string ExtractBase64FromDataUri(string dataUri)
    {
        var index = dataUri.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            throw new InvalidOperationException("Video data URI missing base64 content.");

        return dataUri[(index + "base64,".Length)..];
    }

    private static string? TryGetDataUriMediaType(string dataUri)
    {
        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;
        var separator = dataUri.IndexOf(';');
        return separator > "data:".Length ? dataUri["data:".Length..separator] : null;
    }

    private static T? GetVideoProviderMetadata<T>(VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return default;

        if (!request.ProviderOptions.TryGetValue(providerId, out var element))
            return default;

        if (element.ValueKind == JsonValueKind.Null || element.ValueKind == JsonValueKind.Undefined)
            return default;

        return element.Deserialize<T>(JsonSerializerOptions.Web);
    }
}
