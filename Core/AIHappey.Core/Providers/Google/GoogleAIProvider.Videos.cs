using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private const string GoogleVeoOperationTokenPrefix = "veo_";

    private static readonly JsonSerializerOptions GoogleVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };


    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (request.Model.Contains("omni", StringComparison.OrdinalIgnoreCase))
            return await StartOmniVideoOperation(request, cancellationToken);

        ApplyAuthHeader();
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        var payload = BuildVideoPayload(request, warnings);
        var json = JsonSerializer.Serialize(payload, GoogleVideoJson);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{request.Model}:predictLongRunning")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var operationName = createRoot.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(operationName))
            throw new InvalidOperationException("Google video generation returned no operation name.");

        return new VideoOperationStartResult
        {
            Operation = EncodeVeoOperation(operationName),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
            {
                operation = operationName,
                done = false
            }),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("A video operation is required.", nameof(operation));

        return operation.StartsWith("v1_", StringComparison.OrdinalIgnoreCase)
            ? GetOmniVideoOperationStatus(operation, cancellationToken)
            : GetVeoVideoOperationStatus(operation, cancellationToken);
    }

    private async Task<VideoOperationStatusResult> GetVeoVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        var googleOperation = DecodeVeoOperation(operation);

        using var pollReq = new HttpRequestMessage(HttpMethod.Get, CreateVeoOperationUri(googleOperation));
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);
        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

        using var pollDoc = JsonDocument.Parse(pollRaw);
        var root = pollDoc.RootElement.Clone();
        var done = root.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True;
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(new
        {
            operation,
            done
        });
        
        var response = CreateGoogleVideoResponseData(
            TryGetVeoVideoModel(root) ?? TryGetVeoVideoModelFromOperation(googleOperation));

        if (!done)
        {
            return new VideoOperationPendingResult
            {
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return new VideoOperationErrorResult
            {
                Error = $"Google video generation failed: {errorEl}",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var videoUri = TryGetVideoUri(root);
        if (string.IsNullOrWhiteSpace(videoUri))
        {
            return new VideoOperationErrorResult
            {
                Error = "Google video operation completed but returned no video uri.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        using var downloadReq = new HttpRequestMessage(HttpMethod.Get, videoUri);
        using var downloadResp = await _client.SendAsync(downloadReq, cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
        {
            var raw = await downloadResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google video download failed ({(int)downloadResp.StatusCode}): {raw}");
        }

        var videoBytes = await downloadResp.Content.ReadAsByteArrayAsync(cancellationToken);
        return new VideoOperationCompletedResult
        {
            Videos =
            [
                new VideoOperationVideoData
                {
                    Type = "base64",
                    MediaType = downloadResp.Content.Headers.ContentType?.MediaType ?? "video/mp4",
                    Data = Convert.ToBase64String(videoBytes)
                }
            ],
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private HeaderResponseData CreateGoogleVideoResponseData(string? model = null)
        => new()
        {
            Timestamp = DateTime.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(model)
                ? GetIdentifier()
                : model.ToModelId(GetIdentifier())
        };

    private static string? TryGetVeoVideoModel(JsonElement root)
    {
        if (root.TryGetProperty("metadata", out var metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String)
        {
            return model.GetString();
        }

        return null;
    }

    private static string? TryGetVeoVideoModelFromOperation(string operation)
    {
        const string modelsPrefix = "models/";
        const string operationsSeparator = "/operations/";

        var normalized = operation.Trim().TrimStart('/');
        if (normalized.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["v1beta/".Length..];

        if (!normalized.StartsWith(modelsPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var modelStart = modelsPrefix.Length;
        var operationIndex = normalized.IndexOf(
            operationsSeparator,
            modelStart,
            StringComparison.OrdinalIgnoreCase);

        return operationIndex > modelStart
            ? normalized[modelStart..operationIndex]
            : null;
    }

    private static string CreateVeoOperationUri(string operation)
    {
        var normalized = operation.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            return normalized;

        normalized = normalized.TrimStart('/');
        return normalized.StartsWith("v1beta/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"v1beta/{normalized}";
    }

    private static string EncodeVeoOperation(string operation)
    {
        var bytes = Encoding.UTF8.GetBytes(operation);
        var base64Url = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return GoogleVeoOperationTokenPrefix + base64Url;
    }

    private static string DecodeVeoOperation(string operation)
    {
        if (!operation.StartsWith(GoogleVeoOperationTokenPrefix, StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(operation);

        var base64Url = operation[GoogleVeoOperationTokenPrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        var padding = base64Url.Length % 4;
        if (padding != 0)
            base64Url = base64Url.PadRight(base64Url.Length + (4 - padding), '=');

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64Url));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Google Veo operation token is invalid.", nameof(operation), ex);
        }
    }

    private static Dictionary<string, object?> BuildVideoPayload(VideoRequest request, List<object> warnings)
    {
        var instance = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt
        };

        AddImageInputs(request, instance, warnings);

        var payload = new Dictionary<string, object?>
        {
            ["instances"] = new List<Dictionary<string, object?>>
            {
                instance
            }
        };

        var parameters = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            parameters["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            parameters["aspectRatio"] = request.AspectRatio;

        if (request.Duration is not null)
            parameters["durationSeconds"] = request.Duration;

        if (parameters.Count > 0)
            payload["parameters"] = parameters;

        return payload;
    }

    private static void AddImageInputs(VideoRequest request, Dictionary<string, object?> instance, List<object> warnings)
    {
        var frameImages = request.FrameImages?.ToList() ?? [];
        VideoFile? firstFrame = null;
        VideoFile? lastFrame = null;

        foreach (var frameImage in frameImages)
        {
            if (frameImage?.Image is null)
                throw new InvalidOperationException("Google video frameImages entries must include an image.");

            if (IsFirstFrame(frameImage.FrameType))
            {
                if (firstFrame is not null)
                    throw new InvalidOperationException("Google video generation supports only one first_frame image.");

                firstFrame = frameImage.Image;
            }
            else if (IsLastFrame(frameImage.FrameType))
            {
                if (lastFrame is not null)
                    throw new InvalidOperationException("Google video generation supports only one last_frame image.");

                lastFrame = frameImage.Image;
            }
            else
            {
                throw new InvalidOperationException($"Unsupported Google video frameType '{frameImage.FrameType}'. Use 'first_frame' or 'last_frame'.");
            }
        }

        if (firstFrame is not null)
        {
            instance["image"] = ToGoogleVideoImage(firstFrame);
        }
        else if (request.Image is not null)
        {
            instance["image"] = ToGoogleVideoImage(request.Image);
        }

        if (lastFrame is not null)
            instance["lastFrame"] = ToGoogleVideoInlineData(lastFrame);

        var referenceImages = new List<object>();
        foreach (var reference in request.InputReferences ?? [])
        {
            referenceImages.Add(ToGoogleVideoReferenceImage(reference));
        }

        if (firstFrame is not null && request.Image is not null)
            referenceImages.Add(ToGoogleVideoReferenceImage(request.Image));

        if (referenceImages.Count > 3)
            throw new InvalidOperationException("Google Veo 3.1 video generation supports at most 3 reference images, including top-level image when first_frame is also provided.");

        if (referenceImages.Count > 0)
            instance["referenceImages"] = referenceImages;

        if (frameImages.Count > 0 || referenceImages.Count > 0)
        {
            var hasVeo31Model = request.Model.Contains("veo-3.1", StringComparison.OrdinalIgnoreCase);
            if (!hasVeo31Model)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "veo_3_1_image_inputs",
                    message = "Google reference images and frame images are documented for Veo 3.1 models only."
                });
            }
        }
    }

    private static Dictionary<string, object?> ToGoogleVideoImage(VideoFile image)
        => new()
        {
            ["inlineData"] = ToGoogleVideoInlineData(image)
        };

    private static Dictionary<string, object?> ToGoogleVideoReferenceImage(VideoFile image)
        => new()
        {
            ["image"] = ToGoogleVideoImage(image),
            ["referenceType"] = "asset"
        };

    private static Dictionary<string, object?> ToGoogleVideoInlineData(VideoFile image)
    {
        var (mimeType, data) = NormalizeGoogleVideoImage(image);
        return new Dictionary<string, object?>
        {
            ["mimeType"] = mimeType,
            ["data"] = data
        };
    }

    private static (string MimeType, string Data) NormalizeGoogleVideoImage(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.Data))
            throw new InvalidOperationException("Google video image data is required.");

        var data = image.Data.Trim();
        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Google video generation only supports base64 or data URL image inputs.");
        }

        var mimeType = image.MediaType;
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var commaIndex = data.IndexOf(',');
            if (commaIndex < 0)
                throw new InvalidOperationException("Google video data URL image inputs must include a comma separator.");

            var header = data[5..commaIndex];
            if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Google video data URL image inputs must be base64 encoded.");

            var mimeEnd = header.IndexOf(';');
            if (mimeEnd > 0)
                mimeType = header[..mimeEnd];

            data = data[(commaIndex + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(mimeType))
            throw new InvalidOperationException("Google video image mediaType is required.");

        if (string.IsNullOrWhiteSpace(data))
            throw new InvalidOperationException("Google video image base64 data is required.");

        return (mimeType, data);
    }

    private static bool IsFirstFrame(string? frameType)
        => string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase);

    private static bool IsLastFrame(string? frameType)
        => string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetVideoUri(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            return null;

        if (!response.TryGetProperty("generateVideoResponse", out var generate) || generate.ValueKind != JsonValueKind.Object)
            return null;

        if (!generate.TryGetProperty("generatedSamples", out var samples) || samples.ValueKind != JsonValueKind.Array)
            return null;

        var first = samples.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
            return null;

        if (!first.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
            return null;

        if (!video.TryGetProperty("uri", out var uriEl) || uriEl.ValueKind != JsonValueKind.String)
            return null;

        return uriEl.GetString();
    }
}
