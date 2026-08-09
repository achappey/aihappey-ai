using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.LTX;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LTX;

public partial class LTXProvider
{
    private const string LTXVideoOperationTokenPrefix = "ltxv2_";

    private static readonly JsonSerializerOptions LTXVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record LTXVideoOperationData(
        string JobId,
        string Endpoint,
        string Model,
        string? PreferredResultKey = null);

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var metadata = request.GetProviderMetadata<LTXVideoProviderMetadata>(GetIdentifier());
        var endpoint = ResolveOperation(request, metadata);
        var payload = endpoint switch
        {
            "text-to-video" => BuildTextToVideoPayload(request, metadata),
            "image-to-video" => BuildImageToVideoPayload(request, metadata),
            "audio-to-video" => BuildAudioToVideoPayload(request, metadata),
            "retake" => BuildRetakePayload(request, metadata),
            "extend" => BuildExtendPayload(request, metadata),
            "video-to-video-hdr" => BuildHdrPayload(metadata),
            _ => throw new NotSupportedException($"LTX video operation '{endpoint}' is not supported.")
        };

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"v2/{endpoint}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, LTXVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var submitResponse = await _client.SendAsync(submitRequest, cancellationToken);
        var submitRaw = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (submitResponse.StatusCode != System.Net.HttpStatusCode.Accepted)
            ThrowLTXError($"{endpoint} submit", submitResponse, Encoding.UTF8.GetBytes(submitRaw));

        using var submitDocument = JsonDocument.Parse(submitRaw);
        var submitRoot = submitDocument.RootElement.Clone();
        var jobId = TryGetString(submitRoot, "id");
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException($"LTX {endpoint} submit response missing id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeVideoOperation(new(
                jobId,
                endpoint,
                request.Model,
                metadata?.PreferredResultKey)),
            Warnings = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                id = jobId,
                endpoint,
                status = "pending",
                submit = submitRoot
            }),
            Response = new()
            {
                Timestamp = TryGetDateTime(submitRoot, "created_at") ?? DateTime.UtcNow,
                Headers = submitResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        var operationData = DecodeVideoOperation(operation);
        ApplyAuthHeader();

        using var pollRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v2/{operationData.Endpoint}/{Uri.EscapeDataString(operationData.JobId)}");
        using var pollResponse = await _client.SendAsync(pollRequest, cancellationToken);
        var pollRaw = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResponse.IsSuccessStatusCode)
            ThrowLTXError($"{operationData.Endpoint} status", pollResponse, Encoding.UTF8.GetBytes(pollRaw));

        using var pollDocument = JsonDocument.Parse(pollRaw);
        var root = pollDocument.RootElement.Clone();
        var status = TryGetString(root, "status") ?? "unknown";
        var response = new HeaderResponseData
        {
            Timestamp = TryGetDateTime(root, "completed_at")
                ?? TryGetDateTime(root, "created_at")
                ?? DateTime.UtcNow,
            Headers = pollResponse.GetHeaders(),
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };
        var providerMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            id = operationData.JobId,
            endpoint = operationData.Endpoint,
            status,
            job = root
        });

        if (status.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || status.Equals("processing", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        if (status.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = $"LTX {operationData.Endpoint} job failed (id={operationData.JobId}): {TryGetJobError(root) ?? pollRaw}",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        if (!status.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoOperationErrorResult
            {
                Error = $"LTX {operationData.Endpoint} job returned unknown status '{status}' (id={operationData.JobId}).",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        var resultUrl = TryGetResultUrl(root, operationData.PreferredResultKey);
        if (string.IsNullOrWhiteSpace(resultUrl))
        {
            return new VideoOperationErrorResult
            {
                Error = $"LTX {operationData.Endpoint} job completed without a result URL (id={operationData.JobId}).",
                ProviderMetadata = providerMetadata,
                Response = response
            };
        }

        using var downloadResponse = await _uploadClient.GetAsync(resultUrl, cancellationToken);
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"LTX result download failed ({(int)downloadResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = downloadResponse.Content.Headers.ContentType?.MediaType
                        ?? GuessMediaType(resultUrl)
                        ?? "video/mp4",
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = providerMetadata,
            Response = response
        };
    }

    private static Dictionary<string, object?> BuildTextToVideoPayload(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata)
    {
        var result = new Dictionary<string, object?>
        {
            ["prompt"] = ResolvePrompt(request, metadata, required: true, operation: "text-to-video"),
            ["model"] = ResolveModel(request, metadata, required: true),
            ["duration"] = ResolveIntegerDuration(request, metadata, required: true, operation: "text-to-video"),
            ["resolution"] = ResolveResolution(request, required: true, operation: "text-to-video"),
            ["fps"] = request.Fps
        };

        if (metadata?.GenerateAudio.HasValue == true)
            result["generate_audio"] = metadata.GenerateAudio.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.CameraMotion))
            result["camera_motion"] = metadata.CameraMotion;

        return result;
    }

    private static Dictionary<string, object?> BuildImageToVideoPayload(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata)
    {
        var result = new Dictionary<string, object?>
        {
            ["image_uri"] = ResolveRequiredImageDataUri(request, metadata),
            ["prompt"] = ResolvePrompt(request, metadata, required: true, operation: "image-to-video"),
            ["model"] = ResolveModel(request, metadata, required: true),
            ["duration"] = ResolveIntegerDuration(request, metadata, required: true, operation: "image-to-video"),
            ["resolution"] = ResolveResolution(request, required: true, operation: "image-to-video"),
            ["fps"] = request.Fps
        };

        if (metadata?.GenerateAudio.HasValue == true)
            result["generate_audio"] = metadata.GenerateAudio.Value;
        if (!string.IsNullOrWhiteSpace(metadata?.CameraMotion))
            result["camera_motion"] = metadata.CameraMotion;
        if (!string.IsNullOrWhiteSpace(metadata?.LastFrameData))
            result["last_frame_uri"] = NormalizeDataUri(metadata.LastFrameData, metadata.LastFrameMediaType ?? MediaTypeNames.Image.Png, "last-frame image");

        return result;
    }

    private static Dictionary<string, object?> BuildAudioToVideoPayload(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata?.AudioData))
            throw new ArgumentException("providerOptions.ltx.audio_data is required for LTX audio-to-video.", nameof(request));

        var imageUri = TryResolveImageDataUri(request, metadata);
        return new Dictionary<string, object?>
        {
            ["audio_uri"] = NormalizeDataUri(metadata.AudioData, metadata.AudioMediaType ?? "audio/wav", "audio"),
            ["image_uri"] = imageUri,
            ["prompt"] = ResolvePrompt(request, metadata, required: imageUri is null, operation: "audio-to-video"),
            ["resolution"] = request.Resolution,
            ["guidance_scale"] = metadata.GuidanceScale,
            ["model"] = ResolveModel(request, metadata, required: false)
        };
    }

    private static Dictionary<string, object?> BuildRetakePayload(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata)
    {
        var startTime = metadata?.StartTime
            ?? throw new ArgumentException("providerOptions.ltx.start_time is required for LTX retake.", nameof(request));

        return new Dictionary<string, object?>
        {
            ["video_uri"] = ResolveRequiredVideoDataUri(metadata, request),
            ["start_time"] = startTime,
            ["duration"] = ResolveDoubleDuration(request, metadata, required: true, operation: "retake"),
            ["prompt"] = ResolvePrompt(request, metadata, required: false, operation: "retake"),
            ["mode"] = metadata.Mode,
            ["resolution"] = request.Resolution,
            ["model"] = ResolveModel(request, metadata, required: false)
        };
    }

    private static Dictionary<string, object?> BuildExtendPayload(
        VideoRequest request,
        LTXVideoProviderMetadata? metadata)
        => new()
        {
            ["video_uri"] = ResolveRequiredVideoDataUri(metadata, request),
            ["duration"] = ResolveDoubleDuration(request, metadata, required: true, operation: "extend"),
            ["prompt"] = ResolvePrompt(request, metadata, required: false, operation: "extend"),
            ["mode"] = metadata?.Mode,
            ["model"] = ResolveModel(request, metadata, required: false),
            ["context"] = metadata?.Context
        };

    private static Dictionary<string, object?> BuildHdrPayload(LTXVideoProviderMetadata? metadata)
        => new()
        {
            ["video_uri"] = ResolveRequiredVideoDataUri(metadata, null)
        };

    private static string ResolveRequiredImageDataUri(VideoRequest request, LTXVideoProviderMetadata? metadata)
        => TryResolveImageDataUri(request, metadata)
            ?? throw new ArgumentException("Base64 image input is required for LTX image-to-video.", nameof(request));

    private static string? TryResolveImageDataUri(VideoRequest request, LTXVideoProviderMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.ImageData))
            return NormalizeDataUri(metadata.ImageData, metadata.ImageMediaType ?? MediaTypeNames.Image.Png, "image");
        if (request.Image is null)
            return null;

        return NormalizeDataUri(request.Image.Data, request.Image.MediaType, "image");
    }

    private static string ResolveRequiredVideoDataUri(LTXVideoProviderMetadata? metadata, VideoRequest? request)
    {
        if (string.IsNullOrWhiteSpace(metadata?.VideoData))
            throw new ArgumentException("providerOptions.ltx.video_data is required for this LTX operation.", request is null ? null : nameof(request));

        return NormalizeDataUri(metadata.VideoData, metadata.VideoMediaType ?? "video/mp4", "video");
    }

    private static string NormalizeDataUri(string value, string fallbackMediaType, string inputName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"LTX {inputName} base64 data is required.");

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && !uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"LTX {inputName} accepts base64/data-URI input only; external and storage URIs are not supported.");

        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            Convert.FromBase64String(trimmed);
            return $"data:{fallbackMediaType};base64,{trimmed}";
        }

        var comma = trimmed.IndexOf(',');
        if (comma < 0 || !trimmed[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"LTX {inputName} data URI must contain a base64 payload.");

        Convert.FromBase64String(trimmed[(comma + 1)..]);
        return trimmed;
    }

    private static string EncodeVideoOperation(LTXVideoOperationData operation)
    {
        var json = JsonSerializer.Serialize(operation, LTXVideoJsonOptions);
        return LTXVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static LTXVideoOperationData DecodeVideoOperation(string operation)
    {
        if (!operation.StartsWith(LTXVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The LTX video operation token is invalid.", nameof(operation));

        var base64 = operation[LTXVideoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        if (base64.Length % 4 != 0)
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4), '=');

        try
        {
            var data = JsonSerializer.Deserialize<LTXVideoOperationData>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)),
                LTXVideoJsonOptions);
            if (data is null
                || string.IsNullOrWhiteSpace(data.JobId)
                || string.IsNullOrWhiteSpace(data.Model)
                || !IsSupportedOperation(data.Endpoint))
                throw new ArgumentException("The LTX video operation token is invalid.", nameof(operation));

            return data;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The LTX video operation token is invalid.", nameof(operation), ex);
        }
    }

    private static string ResolveOperation(VideoRequest request, LTXVideoProviderMetadata? metadata)
    {
        var explicitOperation = NormalizeOperation(metadata?.Operation);
        if (!string.IsNullOrWhiteSpace(explicitOperation))
            return explicitOperation;
        if (IsHdrModel(request.Model))
            return "video-to-video-hdr";
        if (!string.IsNullOrWhiteSpace(metadata?.AudioData))
            return "audio-to-video";
        if (request.Image is not null || !string.IsNullOrWhiteSpace(metadata?.ImageData))
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

    private static bool IsSupportedOperation(string operation)
        => operation is "text-to-video" or "image-to-video" or "audio-to-video"
            or "retake" or "extend" or "video-to-video-hdr";

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

        return IsHdrModel(model) || string.IsNullOrWhiteSpace(model) ? null : model;
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

    private static string? TryGetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static DateTime? TryGetDateTime(JsonElement root, string propertyName)
        => DateTime.TryParse(TryGetString(root, propertyName), out var value) ? value : null;

    private static string? TryGetJobError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error))
            return null;
        if (error.ValueKind == JsonValueKind.Object)
            return TryGetString(error, "message") ?? error.ToString();
        return error.ToString();
    }

    private static string? TryGetResultUrl(JsonElement root, string? preferredKey)
    {
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        if (!string.IsNullOrWhiteSpace(preferredKey))
        {
            var preferred = TryGetString(result, preferredKey);
            if (!string.IsNullOrWhiteSpace(preferred))
                return preferred;
        }

        foreach (var key in new[] { "video_url", "exr_frames_url", "output_url", "url", "hdr_video_url" })
        {
            var value = TryGetString(result, key);
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
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
                return error.ValueKind == JsonValueKind.Object ? TryGetString(error, "message") ?? error.ToString() : error.ToString();
            return TryGetString(root, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GuessMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return "application/zip";
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        return null;
    }
}
