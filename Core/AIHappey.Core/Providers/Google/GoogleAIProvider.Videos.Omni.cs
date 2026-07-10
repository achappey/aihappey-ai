using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    private static readonly TimeSpan GoogleOmniVideoFilePollingInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GoogleOmniVideoFilePollingTimeout = TimeSpan.FromMinutes(10);
    private static readonly HashSet<string> GoogleOmniVideoTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        "text_to_video",
        "image_to_video",
        "reference_to_video",
        "edit"
    };


    public async Task<VideoResponse> OmniVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed", details = "Gemini Omni Flash video generation does not document seed support." });

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });

        var payload = BuildOmniVideoPayload(request, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InteractionsRelativeUrl);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);
        httpRequest.Content = new StringContent(payload.ToJsonString(GoogleVideoJson), Encoding.UTF8, MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Omni video failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();

        if (!TryExtractGoogleOmniVideo(root, out var base64, out var videoUri, out var mimeType))
            throw new InvalidOperationException("Google Omni video result contained no video output.");

        if (string.IsNullOrWhiteSpace(base64))
        {
            if (string.IsNullOrWhiteSpace(videoUri))
                throw new InvalidOperationException("Google Omni video result contained no inline data or downloadable uri.");

            (base64, mimeType) = await DownloadGoogleOmniVideoUriAsync(videoUri, mimeType, cancellationToken);
        }

        mimeType = string.IsNullOrWhiteSpace(mimeType) ? "video/mp4" : mimeType;

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["request"] = JsonSerializer.SerializeToElement(payload, GoogleVideoJson),
                ["interaction"] = root.Clone()
            }, JsonSerializerOptions.Web)
        };

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = mimeType,
                    Data = base64
                }
            ],
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model
            }
        };
    }

    private static JsonObject BuildOmniVideoPayload(VideoRequest request, ICollection<object> warnings)
    {
        var metadataElement = request.GetProviderMetadata<JsonElement>(GoogleExtensions.Identifier());
        var metadata = request.GetProviderMetadata<GoogleOmniVideoProviderMetadata>(GoogleExtensions.Identifier());

        var payload = metadataElement.ValueKind == JsonValueKind.Object
            ? CloneJsonObject(metadataElement)
            : [];

        payload["model"] = NormalizeGoogleModelOrAgentId(request.Model);
        payload["input"] = BuildOmniVideoInput(request, metadataElement, warnings);
        payload["stream"] = false;
        payload["background"] = false;
        payload["store"] = false;

        WarnIfBooleanProviderOptionWasTrue(metadataElement, warnings, "stream");
        WarnIfBooleanProviderOptionWasTrue(metadataElement, warnings, "background");
        WarnIfBooleanProviderOptionWasTrue(metadataElement, warnings, "store");

        var previousInteractionId = metadata?.PreviousInteractionId
            ?? metadata?.PreviousInteractionIdCamel
            ?? TryGetGoogleOmniString(metadataElement, "previous_interaction_id", "previousInteractionId");
        if (!string.IsNullOrWhiteSpace(previousInteractionId))
            payload["previous_interaction_id"] = previousInteractionId;
        else
            payload.Remove("previous_interaction_id");

        var responseFormat = ResolveOmniVideoResponseFormat(request, metadataElement, metadata);
        payload["response_format"] = responseFormat;

        var generationConfig = ResolveOmniVideoGenerationConfig(request, metadataElement, metadata, warnings);
        payload["generation_config"] = generationConfig;

        return payload;
    }

    private static JsonNode BuildOmniVideoInput(VideoRequest request, JsonElement metadata, ICollection<object> warnings)
    {
        var content = new JsonArray();
        var hasMedia = false;
        var prompt = request.Prompt.Trim();

        foreach (var providerContent in BuildOmniProviderVideoEditContent(metadata, warnings))
        {
            content.Add(providerContent);
            hasMedia = true;
        }

        var frameImages = request.FrameImages?.ToList() ?? [];
        VideoFile? firstFrame = null;
        var referenceFiles = new List<VideoFile>();

        foreach (var frameImage in frameImages)
        {
            if (frameImage?.Image is null)
                throw new InvalidOperationException("Google Omni video frameImages entries must include an image.");

            if (IsFirstFrame(frameImage.FrameType))
            {
                if (firstFrame is not null)
                    throw new InvalidOperationException("Google Omni video generation supports only one first_frame image.");
                firstFrame = frameImage.Image;
            }
            else if (IsLastFrame(frameImage.FrameType))
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = "frameImages.last_frame",
                    details = "Gemini Omni Flash limitations state that video interpolation / last-frame generation is not supported; the last_frame input was ignored."
                });
            }
            else
            {
                referenceFiles.Add(frameImage.Image);
            }
        }

        if (firstFrame is not null)
        {
            content.Add(ToGoogleOmniImageContent(firstFrame));
            hasMedia = true;
            if (!prompt.Contains("<FIRST_FRAME>", StringComparison.OrdinalIgnoreCase))
                prompt = $"<FIRST_FRAME> {prompt} Use the image as the starting frame.";
        }
        else if (request.Image is not null)
        {
            content.Add(ToGoogleOmniImageContent(request.Image));
            hasMedia = true;
        }

        if (firstFrame is not null && request.Image is not null)
            referenceFiles.Add(request.Image);

        referenceFiles.AddRange(request.InputReferences ?? []);

        foreach (var reference in referenceFiles)
        {
            content.Add(ToGoogleOmniImageContent(reference));
            hasMedia = true;
        }

        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = prompt
        });

        return hasMedia ? content : JsonValue.Create(prompt)!;
    }

    private static IEnumerable<JsonObject> BuildOmniProviderVideoEditContent(JsonElement metadata, ICollection<object> warnings)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            yield break;

        if (TryGetProperty(metadata, "document", out var document) && document.ValueKind == JsonValueKind.Object)
        {
            yield return NormalizeOmniProviderMediaContent(document, "document");
            yield break;
        }

        if (TryGetProperty(metadata, "video", out var video) && video.ValueKind == JsonValueKind.Object)
        {
            yield return NormalizeOmniProviderMediaContent(video, "video");
            yield break;
        }

        var documentUri = TryGetGoogleOmniString(metadata, "document_uri", "documentUri");
        var videoUri = TryGetGoogleOmniString(metadata, "video_uri", "videoUri");
        var documentData = TryGetGoogleOmniString(metadata, "document_data", "documentData");
        var videoData = TryGetGoogleOmniString(metadata, "video_data", "videoData");

        if (!string.IsNullOrWhiteSpace(documentUri) || !string.IsNullOrWhiteSpace(documentData))
        {
            yield return new JsonObject
            {
                ["type"] = "document",
                ["uri"] = documentUri,
                ["data"] = documentData,
                ["mime_type"] = TryGetGoogleOmniString(metadata, "document_mime_type", "documentMimeType", "mime_type", "mimeType") ?? "video/mp4"
            };
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(videoUri) || !string.IsNullOrWhiteSpace(videoData))
        {
            warnings.Add(new
            {
                type = "compatibility",
                feature = "providerOptions.google.video",
                details = "Gemini Omni Flash uploaded video editing is documented through document/file inputs; video content was passed through for compatibility."
            });
            yield return new JsonObject
            {
                ["type"] = "video",
                ["uri"] = videoUri,
                ["data"] = videoData,
                ["mime_type"] = TryGetGoogleOmniString(metadata, "video_mime_type", "videoMimeType", "mime_type", "mimeType") ?? "video/mp4"
            };
        }
    }

    private static JsonObject NormalizeOmniProviderMediaContent(JsonElement source, string fallbackType)
    {
        var content = CloneJsonObject(source);
        if (!HasJsonProperty(content, "type"))
            content["type"] = fallbackType;
        return content;
    }

    private static JsonObject ToGoogleOmniImageContent(VideoFile image)
    {
        var (mimeType, data) = NormalizeGoogleVideoImage(image);
        return new JsonObject
        {
            ["type"] = "image",
            ["data"] = data,
            ["mime_type"] = mimeType
        };
    }

    private static JsonObject ResolveOmniVideoResponseFormat(
        VideoRequest request,
        JsonElement metadataElement,
        GoogleOmniVideoProviderMetadata? metadata)
    {
        var raw = metadata?.ResponseFormat ?? metadata?.ResponseFormatCamel;
        var responseFormat = raw is { ValueKind: JsonValueKind.Object }
            ? CloneJsonObject(raw.Value)
            : TryGetProperty(metadataElement, "response_format", out var snakeResponseFormat) && snakeResponseFormat.ValueKind == JsonValueKind.Object
                ? CloneJsonObject(snakeResponseFormat)
                : TryGetProperty(metadataElement, "responseFormat", out var camelResponseFormat) && camelResponseFormat.ValueKind == JsonValueKind.Object
                    ? CloneJsonObject(camelResponseFormat)
                    : [];

        responseFormat["type"] = "video";

        if (!string.IsNullOrWhiteSpace(request.AspectRatio) && !HasJsonProperty(responseFormat, "aspect_ratio"))
            responseFormat["aspect_ratio"] = request.AspectRatio;

        var delivery = metadata?.Delivery ?? TryGetGoogleOmniString(metadataElement, "delivery");
        if (!string.IsNullOrWhiteSpace(delivery) && !HasJsonProperty(responseFormat, "delivery"))
            responseFormat["delivery"] = delivery.Trim();

        return responseFormat;
    }

    private static JsonObject ResolveOmniVideoGenerationConfig(
        VideoRequest request,
        JsonElement metadataElement,
        GoogleOmniVideoProviderMetadata? metadata,
        ICollection<object> warnings)
    {
        var raw = metadata?.GenerationConfig ?? metadata?.GenerationConfigCamel;
        var generationConfig = raw is { ValueKind: JsonValueKind.Object }
            ? CloneJsonObject(raw.Value)
            : TryGetProperty(metadataElement, "generation_config", out var snakeGenerationConfig) && snakeGenerationConfig.ValueKind == JsonValueKind.Object
                ? CloneJsonObject(snakeGenerationConfig)
                : TryGetProperty(metadataElement, "generationConfig", out var camelGenerationConfig) && camelGenerationConfig.ValueKind == JsonValueKind.Object
                    ? CloneJsonObject(camelGenerationConfig)
                    : [];

        generationConfig["thinking_level"] = "high";

        var videoConfig = GetOrReplaceJsonObject(generationConfig, "video_config");

        var explicitVideoConfig = metadata?.VideoConfig ?? metadata?.VideoConfigCamel;
        if (explicitVideoConfig is { ValueKind: JsonValueKind.Object })
            MergeJsonObject(videoConfig, CloneJsonObject(explicitVideoConfig.Value));
        else if (TryGetProperty(metadataElement, "video_config", out var topLevelSnakeVideoConfig) && topLevelSnakeVideoConfig.ValueKind == JsonValueKind.Object)
            MergeJsonObject(videoConfig, CloneJsonObject(topLevelSnakeVideoConfig));
        else if (TryGetProperty(metadataElement, "videoConfig", out var topLevelCamelVideoConfig) && topLevelCamelVideoConfig.ValueKind == JsonValueKind.Object)
            MergeJsonObject(videoConfig, CloneJsonObject(topLevelCamelVideoConfig));

        var task = ResolveOmniVideoTask(request, metadataElement, videoConfig, metadata, warnings);
        videoConfig["task"] = task;
        generationConfig["video_config"] = videoConfig;

        return generationConfig;
    }

    private static string ResolveOmniVideoTask(
        VideoRequest request,
        JsonElement metadataElement,
        JsonObject videoConfig,
        GoogleOmniVideoProviderMetadata? metadata,
        ICollection<object> warnings)
    {
        var explicitTask = metadata?.Task
            ?? TryGetGoogleOmniString(metadataElement, "task")
            ?? GetJsonObjectString(videoConfig, "task");

        if (!string.IsNullOrWhiteSpace(explicitTask))
        {
            var normalized = explicitTask.Trim();
            if (!GoogleOmniVideoTasks.Contains(normalized))
                throw new InvalidOperationException($"Unsupported Google Omni video task '{normalized}'. Use text_to_video, image_to_video, reference_to_video, or edit.");

            return normalized;
        }

        var hasPreviousInteraction = !string.IsNullOrWhiteSpace(metadata?.PreviousInteractionId)
            || !string.IsNullOrWhiteSpace(metadata?.PreviousInteractionIdCamel)
            || !string.IsNullOrWhiteSpace(TryGetGoogleOmniString(metadataElement, "previous_interaction_id", "previousInteractionId"));
        var hasProviderVideoEditInput = HasOmniProviderVideoEditInput(metadataElement);
        var hasReferences = request.InputReferences?.Any() == true;
        var hasFrameReferences = request.FrameImages?.Any(frame => frame is not null && !IsFirstFrame(frame.FrameType) && !IsLastFrame(frame.FrameType)) == true;
        var hasImage = request.Image is not null || request.FrameImages?.Any(frame => frame is not null && IsFirstFrame(frame.FrameType)) == true;

        if (hasPreviousInteraction || hasProviderVideoEditInput)
            return "edit";

        if (hasReferences || hasFrameReferences || (hasImage && request.InputReferences?.Any() == true))
            return "reference_to_video";

        if (hasImage)
            return "image_to_video";

        return "text_to_video";
    }

    private static bool HasOmniProviderVideoEditInput(JsonElement metadata)
        => metadata.ValueKind == JsonValueKind.Object
           && (TryGetProperty(metadata, "document", out _)
               || TryGetProperty(metadata, "video", out _)
               || !string.IsNullOrWhiteSpace(TryGetGoogleOmniString(metadata, "document_uri", "documentUri", "document_data", "documentData", "video_uri", "videoUri", "video_data", "videoData")));

    private static bool TryExtractGoogleOmniVideo(JsonElement root, out string? base64, out string? uri, out string? mimeType)
    {
        base64 = null;
        uri = null;
        mimeType = null;
        return TryFindOmniVideoObject(root, out base64, out uri, out mimeType);
    }

    private static bool TryFindOmniVideoObject(JsonElement element, out string? base64, out string? uri, out string? mimeType)
    {
        base64 = null;
        uri = null;
        mimeType = null;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryExtractVideoFromObject(element, out base64, out uri, out mimeType))
                    return true;

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindOmniVideoObject(property.Value, out base64, out uri, out mimeType))
                        return true;
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindOmniVideoObject(item, out base64, out uri, out mimeType))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryExtractVideoFromObject(JsonElement element, out string? base64, out string? uri, out string? mimeType)
    {
        base64 = null;
        uri = null;
        mimeType = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        var type = TryGetString(element, "type");
        var mime = TryGetString(element, "mime_type") ?? TryGetString(element, "mimeType");
        var data = TryGetString(element, "data");
        var foundUri = TryGetString(element, "uri");

        if (!string.Equals(type, "video", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(mime) || !mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(data) && string.IsNullOrWhiteSpace(foundUri))
            return false;

        base64 = data;
        uri = foundUri;
        mimeType = mime;
        return true;
    }

    private async Task<(string Base64, string MimeType)> DownloadGoogleOmniVideoUriAsync(
        string uri,
        string? mimeType,
        CancellationToken cancellationToken)
    {
        var fileName = TryExtractGoogleFileName(uri);
        if (!string.IsNullOrWhiteSpace(fileName))
            await WaitForGoogleOmniFileActiveAsync(fileName, cancellationToken);

        var downloadUri = CreateGoogleOmniDownloadUri(uri, fileName);
        using var downloadReq = new HttpRequestMessage(HttpMethod.Get, downloadUri);
        using var downloadResp = await _client.SendAsync(downloadReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
        {
            var raw = await downloadResp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Google Omni video download failed ({(int)downloadResp.StatusCode}): {raw}");
        }

        var bytes = await downloadResp.Content.ReadAsByteArrayAsync(cancellationToken);
        var resolvedMimeType = downloadResp.Content.Headers.ContentType?.MediaType ?? mimeType ?? "video/mp4";
        return (Convert.ToBase64String(bytes), resolvedMimeType);
    }

    private async Task WaitForGoogleOmniFileActiveAsync(string fileName, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + GoogleOmniVideoFilePollingTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var statusReq = new HttpRequestMessage(HttpMethod.Get, $"v1beta/{fileName}");
            using var statusResp = await _client.SendAsync(statusReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!statusResp.IsSuccessStatusCode)
                return;

            var raw = await statusResp.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var state = TryGetGoogleFileState(document.RootElement);
            if (string.Equals(state, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.Equals(state, "FAILED", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Google Omni video file '{fileName}' failed processing.");

            await Task.Delay(GoogleOmniVideoFilePollingInterval, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for Google Omni video file '{fileName}' to become ACTIVE.");
    }

    private static string? TryGetGoogleFileState(JsonElement root)
    {
        if (!TryGetProperty(root, "state", out var state))
            return null;

        if (state.ValueKind == JsonValueKind.String)
            return state.GetString();

        if (state.ValueKind == JsonValueKind.Object)
            return TryGetString(state, "name") ?? TryGetString(state, "state");

        return null;
    }

    private static string CreateGoogleOmniDownloadUri(string uri, string? fileName)
    {
        if (uri.Contains(":download", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(fileName))
            return uri;

        return $"v1beta/{fileName}:download?alt=media";
    }

    private static string? TryExtractGoogleFileName(string uri)
    {
        var match = Regex.Match(uri, @"files/([^/:?]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"files/{match.Groups[1].Value}" : null;
    }

    private static void WarnIfBooleanProviderOptionWasTrue(JsonElement metadata, ICollection<object> warnings, string propertyName)
    {
        if (!TryGetProperty(metadata, propertyName, out var property)
            || property.ValueKind != JsonValueKind.True)
        {
            return;
        }

        warnings.Add(new
        {
            type = "ignored",
            feature = $"providerOptions.google.{propertyName}",
            reason = "Google Omni video endpoint forces fast synchronous non-streaming generation with storage disabled. Use the chat/interactions path for conversational stored editing."
        });
    }

    private static JsonObject GetOrReplaceJsonObject(JsonObject obj, string propertyName)
    {
        foreach (var property in obj.ToList())
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value is JsonObject existing)
                return existing;

            obj.Remove(property.Key);
            break;
        }

        var created = new JsonObject();
        obj[propertyName] = created;
        return created;
    }

    private static void MergeJsonObject(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
            target[property.Key] = property.Value?.DeepClone();
    }

    private static string? GetJsonObjectString(JsonObject obj, string propertyName)
    {
        foreach (var property in obj)
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value is null ? null : property.Value.GetValueKind() == JsonValueKind.String
                ? property.Value.GetValue<string>()
                : property.Value.ToJsonString();
        }

        return null;
    }

    private static string? TryGetGoogleOmniString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var propertyName in propertyNames)
        {
            var value = TryGetString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

}
