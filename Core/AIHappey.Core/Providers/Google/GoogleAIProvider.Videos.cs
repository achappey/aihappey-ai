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
    private static readonly JsonSerializerOptions GoogleVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Google API key.");

        if (request.Model.Contains("omni"))
            return await OmniVideoRequest(request, cancellationToken);

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/")
        };

        var payload = BuildVideoPayload(request, warnings);
        var json = JsonSerializer.Serialize(payload, GoogleVideoJson);

        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"models/{request.Model}:predictLongRunning")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        createReq.Headers.Add("x-goog-api-key", key);

        using var createResp = await http.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google video create failed ({(int)createResp.StatusCode}): {createRaw}");

        using var createDoc = JsonDocument.Parse(createRaw);
        var createRoot = createDoc.RootElement.Clone();
        var operationName = createRoot.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(operationName))
            throw new InvalidOperationException("Google video generation returned no operation name.");

        var final = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async token =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, operationName);
                pollReq.Headers.Add("x-goog-api-key", key);
                using var pollResp = await http.SendAsync(pollReq, token);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(token);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Google video poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var pollDoc = JsonDocument.Parse(pollRaw);
                return pollDoc.RootElement.Clone();
            },
            result => result.TryGetProperty("done", out var doneEl) && doneEl.ValueKind == JsonValueKind.True,
            interval: TimeSpan.FromSeconds(5),
            timeout: null,
            maxAttempts: null,
            cancellationToken: cancellationToken);

        if (final.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Google video generation failed: {errorEl}");

        var videoUri = TryGetVideoUri(final);
        if (string.IsNullOrWhiteSpace(videoUri))
            throw new InvalidOperationException("Google video result contained no video uri.");

        using var downloadReq = new HttpRequestMessage(HttpMethod.Get, videoUri);
        downloadReq.Headers.Add("x-goog-api-key", key);
        using var downloadResp = await http.SendAsync(downloadReq, cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
        {
            var raw = await downloadResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google video download failed ({(int)downloadResp.StatusCode}): {raw}");
        }

        var videoBytes = await downloadResp.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = downloadResp.Content.Headers.ContentType?.MediaType ?? "video/mp4";
     
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
            ProviderMetadata = GoogleExtensions.Identifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
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
