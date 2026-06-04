using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.LTX;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LTX;

public partial class LTXProvider
{
    private static readonly JsonSerializerOptions LTXVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record LTXAsyncJobResult(string Status, string Raw, JsonElement Root);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<LTXVideoProviderMetadata>(GetIdentifier());
        var operation = ResolveOperation(request, metadata);

        return operation switch
        {
            "text-to-video" => await SendSynchronousVideoRequestAsync(
                request,
                operation,
                "v1/text-to-video",
                await BuildTextToVideoPayloadAsync(request, metadata, cancellationToken),
                warnings,
                now,
                cancellationToken),
            "image-to-video" => await SendSynchronousVideoRequestAsync(
                request,
                operation,
                "v1/image-to-video",
                await BuildImageToVideoPayloadAsync(request, metadata, cancellationToken),
                warnings,
                now,
                cancellationToken),
            "audio-to-video" => await SendSynchronousVideoRequestAsync(
                request,
                operation,
                "v1/audio-to-video",
                await BuildAudioToVideoPayloadAsync(request, metadata, cancellationToken),
                warnings,
                now,
                cancellationToken),
            "retake" => await SendSynchronousVideoRequestAsync(
                request,
                operation,
                "v1/retake",
                await BuildRetakePayloadAsync(request, metadata, cancellationToken),
                warnings,
                now,
                cancellationToken),
            "extend" => await SendSynchronousVideoRequestAsync(
                request,
                operation,
                "v1/extend",
                await BuildExtendPayloadAsync(request, metadata, cancellationToken),
                warnings,
                now,
                cancellationToken),
            "video-to-video-hdr" => await SendHdrRequestAsync(
                request,
                metadata,
                warnings,
                now,
                cancellationToken),
            _ => throw new NotSupportedException($"LTX video operation '{operation}' is not supported.")
        };
    }

    private async Task<VideoResponse> SendSynchronousVideoRequestAsync(
        VideoRequest request,
        string operation,
        string endpoint,
        Dictionary<string, object?> payload,
        List<object> warnings,
        DateTime timestamp,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, LTXVideoJsonOptions);
        using var ltxRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(ltxRequest, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            ThrowLTXError(operation, response, bytes);

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessMediaType(endpoint)
            ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mediaType,
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = ToJsonElement(new
                {
                    operation,
                    endpoint,
                    payload
                })
            },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = new
                {
                    operation,
                    endpoint,
                    payload
                }
            }
        };
    }

    private async Task<VideoResponse> SendHdrRequestAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        List<object> warnings,
        DateTime timestamp,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["video_uri"] = await ResolveVideoUriAsync(metadata, cancellationToken)
        };

        var json = JsonSerializer.Serialize(payload, LTXVideoJsonOptions);
        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, "v2/video-to-video-hdr")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            ThrowLTXError("video-to-video-hdr", submitResponse, Encoding.UTF8.GetBytes(submitRaw));

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var submitRoot = submitDoc.RootElement.Clone();
        var jobId = submitRoot.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("LTX HDR submit response missing id.");

        var pollInterval = TimeSpan.FromSeconds(metadata?.PollIntervalSeconds is > 0 ? metadata.PollIntervalSeconds.Value : 5);
        var timeout = TimeSpan.FromSeconds(metadata?.TimeoutSeconds is > 0 ? metadata.TimeoutSeconds.Value : 600);

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            poll: ct => PollHdrJobAsync(jobId, ct),
            isTerminal: r => IsTerminalStatus(r.Status),
            interval: pollInterval,
            timeout: timeout,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (completed.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"LTX HDR job failed (id={jobId}): {TryGetJobError(completed.Root) ?? completed.Raw}");

        var resultUrl = TryGetResultUrl(completed.Root, metadata?.PreferredResultKey);
        List<VideoResponseFile> files = [];

        if (!string.IsNullOrWhiteSpace(resultUrl))
        {
            using var fileResponse = await _uploadClient.GetAsync(resultUrl, cancellationToken);
            var fileBytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!fileResponse.IsSuccessStatusCode)
                throw new InvalidOperationException($"LTX HDR result download failed ({(int)fileResponse.StatusCode}): {Encoding.UTF8.GetString(fileBytes)}");

            files.Add(new VideoResponseFile
            {
                MediaType = fileResponse.Content.Headers.ContentType?.MediaType
                    ?? GuessMediaType(resultUrl)
                    ?? "application/octet-stream",
                Data = Convert.ToBase64String(fileBytes)
            });
        }
        else
        {
            warnings.Add(new { type = "missing_result_url", feature = "video-to-video-hdr" });
        }

        return new VideoResponse
        {
            Videos = files,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = ToJsonElement(new
                {
                    operation = "video-to-video-hdr",
                    endpoint = "v2/video-to-video-hdr",
                    payload,
                    submit = submitRoot,
                    final = completed.Root,
                    downloaded_result_url = resultUrl
                })
            },
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = request.Model,
                Body = submitRoot
            }
        };
    }

    private async Task<Dictionary<string, object?>> BuildTextToVideoPayloadAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var prompt = ResolvePrompt(request, metadata, required: true, operation: "text-to-video");
        var model = ResolveModel(request, metadata, required: true);
        var duration = ResolveIntegerDuration(request, metadata, required: true, operation: "text-to-video");
        var resolution = ResolveResolution(request, required: true, operation: "text-to-video");

        await Task.CompletedTask;

        var result = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["model"] = model,
            ["duration"] = duration,
            ["fps"] = request.Fps,
            ["resolution"] = resolution
        };

        if (metadata?.GenerateAudio.HasValue == true)
            result["generate_audio"] = metadata?.GenerateAudio;

        if (!string.IsNullOrEmpty(metadata?.CameraMotion))
            result["camera_motion"] = metadata?.CameraMotion;

        return result;
    }

    private async Task<Dictionary<string, object?>> BuildImageToVideoPayloadAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var prompt = ResolvePrompt(request, metadata, required: true, operation: "image-to-video");
        var model = ResolveModel(request, metadata, required: true);
        var duration = ResolveIntegerDuration(request, metadata, required: true, operation: "image-to-video");
        var resolution = ResolveResolution(request, required: true, operation: "image-to-video");
        var imageUri = await ResolveImageUriAsync(request, metadata, cancellationToken);
        var lastFrameUri = await ResolveLastFrameUriAsync(metadata, cancellationToken);

        var result = new Dictionary<string, object?>
        {
            ["image_uri"] = imageUri,
            ["prompt"] = prompt,
            ["model"] = model,
            ["duration"] = duration,
            ["fps"] = request.Fps,
            ["resolution"] = resolution
        };

        if (metadata?.GenerateAudio.HasValue == true)
            result["generate_audio"] = metadata?.GenerateAudio;

        if (!string.IsNullOrEmpty(metadata?.CameraMotion))
            result["camera_motion"] = metadata?.CameraMotion;

        if (!string.IsNullOrEmpty(lastFrameUri))
            result["last_frame_uri"] = lastFrameUri;

        return result;

    }

    private async Task<Dictionary<string, object?>> BuildAudioToVideoPayloadAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var audioUri = await ResolveAudioUriAsync(metadata, cancellationToken);
        var imageUri = await TryResolveImageUriAsync(request, metadata, cancellationToken);
        var prompt = ResolvePrompt(request, metadata, required: string.IsNullOrWhiteSpace(imageUri), operation: "audio-to-video");

        return new Dictionary<string, object?>
        {
            ["audio_uri"] = audioUri,
            ["image_uri"] = imageUri,
            ["prompt"] = prompt,
            ["resolution"] = request.Resolution,
            ["guidance_scale"] = metadata?.GuidanceScale,
            ["model"] = ResolveModel(request, metadata, required: false)
        };
    }

    private async Task<Dictionary<string, object?>> BuildRetakePayloadAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var startTime = metadata?.StartTime
            ?? throw new ArgumentException("providerOptions.ltx.start_time is required for LTX retake.", nameof(request));
        var duration = ResolveDoubleDuration(request, metadata, required: true, operation: "retake");

        return new Dictionary<string, object?>
        {
            ["video_uri"] = await ResolveVideoUriAsync(metadata, cancellationToken),
            ["prompt"] = ResolvePrompt(request, metadata, required: false, operation: "retake"),
            ["start_time"] = startTime,
            ["duration"] = duration,
            ["mode"] = metadata?.Mode,
            ["resolution"] = request.Resolution,
            ["model"] = ResolveModel(request, metadata, required: false)
        };
    }

    private async Task<Dictionary<string, object?>> BuildExtendPayloadAsync(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var duration = ResolveDoubleDuration(request, metadata, required: true, operation: "extend");

        return new Dictionary<string, object?>
        {
            ["video_uri"] = await ResolveVideoUriAsync(metadata, cancellationToken),
            ["prompt"] = ResolvePrompt(request, metadata, required: false, operation: "extend"),
            ["duration"] = duration,
            ["mode"] = metadata?.Mode,
            ["model"] = ResolveModel(request, metadata, required: false),
            ["context"] = metadata?.Context
        };
    }

    private async Task<string> ResolveImageUriAsync(VideoRequest request, LTXVideoProviderMetadata? metadata, CancellationToken cancellationToken)
        => await TryResolveImageUriAsync(request, metadata, cancellationToken)
            ?? throw new ArgumentException("Image input is required for LTX image-to-video.", nameof(request));

    private async Task<string?> TryResolveImageUriAsync(VideoRequest request, LTXVideoProviderMetadata? metadata, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.ImageUri))
            return metadata.ImageUri;

        if (!string.IsNullOrWhiteSpace(metadata?.ImageData))
            return await ResolveUploadedOrRemoteUriAsync(metadata.ImageData, metadata.ImageMediaType ?? MediaTypeNames.Image.Png, cancellationToken);

        if (request.Image is null)
            return null;

        return await ResolveUploadedOrRemoteUriAsync(request.Image.Data, request.Image.MediaType, cancellationToken);
    }

    private async Task<string?> ResolveLastFrameUriAsync(LTXVideoProviderMetadata? metadata, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.LastFrameUri))
            return metadata.LastFrameUri;

        if (string.IsNullOrWhiteSpace(metadata?.LastFrameData))
            return null;

        return await ResolveUploadedOrRemoteUriAsync(metadata.LastFrameData, metadata.LastFrameMediaType ?? MediaTypeNames.Image.Png, cancellationToken);
    }

    private async Task<string> ResolveAudioUriAsync(LTXVideoProviderMetadata? metadata, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.AudioUri))
            return metadata.AudioUri;

        if (string.IsNullOrWhiteSpace(metadata?.AudioData))
            throw new ArgumentException("providerOptions.ltx.audio_uri or providerOptions.ltx.audio_data is required for LTX audio-to-video.");

        return await ResolveUploadedOrRemoteUriAsync(metadata.AudioData, metadata.AudioMediaType ?? "audio/wav", cancellationToken);
    }

    private async Task<string> ResolveVideoUriAsync(LTXVideoProviderMetadata? metadata, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.VideoUri))
            return metadata.VideoUri;

        if (string.IsNullOrWhiteSpace(metadata?.VideoData))
            throw new ArgumentException("providerOptions.ltx.video_uri or providerOptions.ltx.video_data is required for this LTX operation.");

        return await ResolveUploadedOrRemoteUriAsync(metadata.VideoData, metadata.VideoMediaType ?? "video/mp4", cancellationToken);
    }

    private async Task<string> ResolveUploadedOrRemoteUriAsync(string value, string mediaType, CancellationToken cancellationToken)
    {
        if (IsRemoteOrStorageUri(value))
            return value;

        var (bytes, detectedMediaType) = DecodeMediaPayload(value, mediaType);
        return await UploadMediaAsync(bytes, detectedMediaType, cancellationToken);
    }

    private async Task<string> UploadMediaAsync(byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v1/upload");
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            ThrowLTXError("upload", createResponse, Encoding.UTF8.GetBytes(createRaw));

        using var uploadDoc = JsonDocument.Parse(createRaw);
        var root = uploadDoc.RootElement;
        var uploadUrl = root.TryGetProperty("upload_url", out var uploadUrlEl) && uploadUrlEl.ValueKind == JsonValueKind.String
            ? uploadUrlEl.GetString()
            : null;
        var storageUri = root.TryGetProperty("storage_uri", out var storageUriEl) && storageUriEl.ValueKind == JsonValueKind.String
            ? storageUriEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(storageUri))
            throw new InvalidOperationException("LTX upload response missing upload_url or storage_uri.");

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(bytes)
        };

        uploadRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        if (root.TryGetProperty("required_headers", out var headersEl) && headersEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headersEl.EnumerateObject())
            {
                if (header.Value.ValueKind != JsonValueKind.String)
                    continue;

                AddUploadHeader(uploadRequest, header.Name, header.Value.GetString()!);
            }
        }

        using var uploadResponse = await _uploadClient.SendAsync(uploadRequest, cancellationToken);
        var uploadBytes = await uploadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!uploadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"LTX signed upload failed ({(int)uploadResponse.StatusCode}): {Encoding.UTF8.GetString(uploadBytes)}");

        return storageUri;
    }

    private async Task<LTXAsyncJobResult> PollHdrJobAsync(string jobId, CancellationToken cancellationToken)
    {
        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"v2/video-to-video-hdr/{Uri.EscapeDataString(jobId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            ThrowLTXError("video-to-video-hdr status", pollResponse, Encoding.UTF8.GetBytes(pollRaw));

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString() ?? "unknown"
            : "unknown";

        return new LTXAsyncJobResult(status, pollRaw, root);
    }

    private static string ResolveOperation(VideoRequest request, LTXVideoProviderMetadata? metadata)
    {
        var explicitOperation = NormalizeOperation(metadata?.Operation);
        if (!string.IsNullOrWhiteSpace(explicitOperation))
            return explicitOperation;

        if (IsHdrModel(request.Model))
            return "video-to-video-hdr";

        if (!string.IsNullOrWhiteSpace(metadata?.AudioUri) || !string.IsNullOrWhiteSpace(metadata?.AudioData))
            return "audio-to-video";

        if (request.Image is not null || !string.IsNullOrWhiteSpace(metadata?.ImageUri) || !string.IsNullOrWhiteSpace(metadata?.ImageData))
            return "image-to-video";

        return "text-to-video";
    }

    private static string? NormalizeOperation(string? operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            return null;

        return operation.Trim().ToLowerInvariant() switch
        {
            "t2v" or "text" or "text_to_video" or "text-to-video" => "text-to-video",
            "i2v" or "image" or "image_to_video" or "image-to-video" => "image-to-video",
            "a2v" or "audio" or "audio_to_video" or "audio-to-video" => "audio-to-video",
            "edit" or "retake" => "retake",
            "extend" => "extend",
            "hdr" or "video_to_video_hdr" or "video-to-video-hdr" => "video-to-video-hdr",
            var value => value
        };
    }

    private static string ResolvePrompt(VideoRequest request, LTXVideoProviderMetadata? metadata, bool required, string operation)
    {
        var prompt = !string.IsNullOrWhiteSpace(metadata?.Prompt) ? metadata.Prompt : request.Prompt;
        if (required && string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException($"Prompt is required for LTX {operation}.", nameof(request));

        return string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt;
    }

    private static string? ResolveModel(VideoRequest request, LTXVideoProviderMetadata? metadata, bool required)
    {
        var model = NormalizeModelName(!string.IsNullOrWhiteSpace(metadata?.Model) ? metadata.Model! : request.Model);
        if (required && string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required for LTX video generation.", nameof(request));

        if (IsHdrModel(model))
            return null;

        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    private static int? ResolveIntegerDuration(VideoRequest request, LTXVideoProviderMetadata? metadata, bool required, string operation)
    {
        var duration = metadata?.Duration is not null
            ? (int)Math.Round(metadata.Duration.Value, MidpointRounding.AwayFromZero)
            : request.Duration;

        if (required && duration is null)
            throw new ArgumentException($"Duration is required for LTX {operation}.", nameof(request));

        return duration;
    }

    private static double? ResolveDoubleDuration(VideoRequest request, LTXVideoProviderMetadata? metadata, bool required, string operation)
    {
        var duration = metadata?.Duration ?? request.Duration;
        if (required && duration is null)
            throw new ArgumentException($"Duration is required for LTX {operation}.", nameof(request));

        return duration;
    }

    private static string? ResolveResolution(VideoRequest request, bool required, string operation)
    {
        if (required && string.IsNullOrWhiteSpace(request.Resolution))
            throw new ArgumentException($"Resolution is required for LTX {operation}.", nameof(request));

        return request.Resolution;
    }

    private static string NormalizeModelName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool IsHdrModel(string? model)
    {
        var normalized = NormalizeModelName(model);
        return normalized.Equals("video-to-video-hdr", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("hdr", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("video-to-video-hdr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemoteOrStorageUri(string value)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ltx://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("gs://", StringComparison.OrdinalIgnoreCase);

    private static (byte[] Bytes, string MediaType) DecodeMediaPayload(string value, string fallbackMediaType)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = value.IndexOf(',');
            if (commaIndex < 0)
                throw new ArgumentException("Invalid data URL supplied to LTX upload.");

            var header = value[5..commaIndex];
            var semicolon = header.IndexOf(';');
            var mediaType = semicolon >= 0 ? header[..semicolon] : header;
            if (string.IsNullOrWhiteSpace(mediaType))
                mediaType = fallbackMediaType;

            var payload = value[(commaIndex + 1)..];
            return (Convert.FromBase64String(payload), mediaType);
        }

        return (Convert.FromBase64String(value), fallbackMediaType);
    }

    private static void AddUploadHeader(HttpRequestMessage request, string name, string value)
    {
        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
        {
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue(value);
            return;
        }

        if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            request.Content!.Headers.TryAddWithoutValidation(name, value);
        else
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private static bool IsTerminalStatus(string status)
        => status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetJobError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var errorEl))
        {
            if (errorEl.ValueKind == JsonValueKind.Object
                && errorEl.TryGetProperty("message", out var messageEl)
                && messageEl.ValueKind == JsonValueKind.String)
                return messageEl.GetString();

            return errorEl.ToString();
        }

        return null;
    }

    private static string? TryGetResultUrl(JsonElement root, string? preferredKey)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredKey)
            && result.TryGetProperty(preferredKey, out var preferred)
            && preferred.ValueKind == JsonValueKind.String)
        {
            var value = preferred.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        foreach (var key in new[] { "video_url", "output_url", "url", "hdr_video_url", "exr_frames_url" })
        {
            if (result.TryGetProperty(key, out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var value = urlEl.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        foreach (var property in result.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            var value = property.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static void ThrowLTXError(string operation, HttpResponseMessage response, byte[] bytes)
    {
        var raw = Encoding.UTF8.GetString(bytes);
        var message = TryParseErrorMessage(raw) ?? raw;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
            ? $"LTX {operation} failed ({(int)response.StatusCode})."
            : $"LTX {operation} failed ({(int)response.StatusCode}): {message}");
    }

    private static string? TryParseErrorMessage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var messageEl)
                    && messageEl.ValueKind == JsonValueKind.String)
                    return messageEl.GetString();

                return error.ToString();
            }

            if (root.TryGetProperty("message", out var directMessage) && directMessage.ValueKind == JsonValueKind.String)
                return directMessage.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static JsonElement ToJsonElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, LTXVideoJsonOptions);

    private static string? GuessMediaType(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
            return null;

        if (urlOrPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return "application/zip";
        if (urlOrPath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        if (urlOrPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";
        if (urlOrPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        return null;
    }
}
