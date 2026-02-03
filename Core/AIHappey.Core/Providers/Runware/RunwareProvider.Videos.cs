using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider : IModelProvider
{
    private static readonly JsonSerializerOptions RunwareVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
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

        var resolvedTaskUuid = TryGetTaskUuid(createRaw) ?? taskUuid;

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                var pollPayload = new Dictionary<string, object?>
                {
                    ["taskType"] = "getResponse",
                    ["taskUUID"] = resolvedTaskUuid
                };

                var pollJson = JsonSerializer.Serialize(new[] { pollPayload }, RunwareVideoJson);
                using var pollResp = await _client.PostAsync(
                    "",
                    new StringContent(pollJson, Encoding.UTF8, MediaTypeNames.Application.Json),
                    token);

                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Runware getResponse failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return (root: pollDoc.RootElement.Clone(), raw: pollRaw);
            },
            result =>
            {
                var status = TryGetStatus(result.root);
                return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(5),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var finalStatus = TryGetStatus(completed.root);
        if (!string.Equals(finalStatus, "success", StringComparison.OrdinalIgnoreCase))
        {
            var error = TryGetError(completed.root);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"Runware video inference failed with status '{finalStatus}'."
                : $"Runware video inference failed with status '{finalStatus}': {error}");
        }

        var (videoBytes, mediaType) = await ResolveVideoBytesAsync(completed.root, metadata, cancellationToken);

        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = completed.root.Clone()
            };
        }
        catch
        {
            // best-effort only
        }

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.root.Clone()
            }
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

        AddIfNotNull(payload, "deliveryMethod", metadata?.DeliveryMethod);
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

    private async Task<(byte[] bytes, string mediaType)> ResolveVideoBytesAsync(
        JsonElement root,
        RunwareProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var data = TryGetFirstDataElement(root);
        if (data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Runware video response contained no data element.");

        if (data.TryGetProperty("videoURL", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
        {
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                throw new InvalidOperationException("Runware video response contained an empty videoURL.");

            var bytes = await _client.GetByteArrayAsync(url, cancellationToken);
            var mediaType = GuessVideoMediaType(url)
                ?? OutputFormatToVideoMime(metadata?.OutputFormat)
                ?? "video/mp4";

            return (bytes, mediaType);
        }

        if (data.TryGetProperty("videoDataURI", out var dataUriEl) && dataUriEl.ValueKind == JsonValueKind.String)
        {
            var dataUri = dataUriEl.GetString();
            if (string.IsNullOrWhiteSpace(dataUri))
                throw new InvalidOperationException("Runware video response contained an empty videoDataURI.");

            var base64 = ExtractBase64FromDataUri(dataUri);
            return (Convert.FromBase64String(base64), OutputFormatToVideoMime(metadata?.OutputFormat) ?? "video/mp4");
        }

        if (data.TryGetProperty("videoBase64Data", out var b64El) && b64El.ValueKind == JsonValueKind.String)
        {
            var base64 = b64El.GetString();
            if (string.IsNullOrWhiteSpace(base64))
                throw new InvalidOperationException("Runware video response contained empty videoBase64Data.");

            return (Convert.FromBase64String(base64), OutputFormatToVideoMime(metadata?.OutputFormat) ?? "video/mp4");
        }

        throw new InvalidOperationException("Runware video response contained no video URL or base64 data.");
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
        var data = TryGetFirstDataElement(root);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;
    }

    private static string? TryGetError(JsonElement root)
    {
        var data = TryGetFirstDataElement(root);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.String
                ? errorEl.GetString()
                : null;
    }

    private static string? TryGetTaskUuid(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var data = TryGetFirstDataElement(doc.RootElement);
        return data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("taskUUID", out var taskEl)
            && taskEl.ValueKind == JsonValueKind.String
                ? taskEl.GetString()
                : null;
    }

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
