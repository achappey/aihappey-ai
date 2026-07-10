using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider
{
    private const string RunwayAvatarModel = "avatar";
    private const string RunwayAvatarApiModel = "gwm1_avatars";
    private const string RunwayAvatarEndpoint = "v1/avatar_videos";
    private const string RunwayVideoUpscaleModel = "magnific_video_upscaler_creative";
    private const string RunwayVideoUpscaleEndpoint = "v1/video_upscale";

    private static readonly HashSet<string> RunwayPresetAvatarIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "game-character",
        "music-superstar",
        "game-character-man",
        "cat-character",
        "influencer",
        "tennis-coach",
        "human-resource",
        "fashion-designer",
        "cooking-teacher"
    };

    private static readonly JsonSerializerOptions VideoJsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "fps"
            });
        }

        if (request.N is not null && request.N > 1)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "n"
            });
        }

        var avatarRoute = ResolveAvatarRoute(request.Model);
        var isVideoUpscale = IsVideoUpscaleModel(request.Model);

        Dictionary<string, object?> payload;
        string endpoint;

        if (isVideoUpscale)
        {
            AddVideoUpscaleUnsupportedWarnings(request, warnings);
            payload = BuildVideoUpscalePayload(request, warnings);
            endpoint = RunwayVideoUpscaleEndpoint;
        }
        else if (avatarRoute.IsAvatar)
        {
            AddAvatarUnsupportedWarnings(request, warnings);
            payload = BuildAvatarVideoPayload(request, avatarRoute, warnings);
            endpoint = RunwayAvatarEndpoint;
        }
        else
        {
            var hasInput = request.Image is not null;
            var mediaType = request.Image?.MediaType ?? string.Empty;
            var isImage = hasInput && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            var isVideo = hasInput && mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

            payload = BuildVideoPayload(request, isImage, isVideo, warnings);
            endpoint = ResolveVideoEndpoint(isImage, isVideo);
        }

        var json = JsonSerializer.Serialize(payload, VideoJsonOpts);
        using var resp = await _client.PostAsync(
            endpoint,
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runway video request failed ({(int)resp.StatusCode}): {body}");

        var node = JsonNode.Parse(body);
        var taskId = ExtractTaskId(node);

        var (bytes, mimeType, outputUrl, lastResult) = await WaitForTaskAndDownloadFirstOutputAsync(taskId, cancellationToken);
        var resolvedMime = !string.IsNullOrWhiteSpace(mimeType)
            ? mimeType!
            : GuessMimeFromUrl(outputUrl) ?? "video/mp4";

        return new VideoResponse
        {
            Videos =
            [
                new VideoResponseFile
                {
                    MediaType = resolvedMime,
                    Data = Convert.ToBase64String(bytes)
                }
            ],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    private static string ResolveVideoEndpoint(bool isImage, bool isVideo)
    {
        if (isVideo)
            return "v1/video_to_video";

        if (isImage)
            return "v1/image_to_video";

        return "v1/text_to_video";
    }

    private static Dictionary<string, object?> BuildVideoPayload(
        VideoRequest request,
        bool isImage,
        bool isVideo,
        List<object> warnings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
        };

        if (!string.IsNullOrWhiteSpace(request.Prompt))
            payload["promptText"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            payload["ratio"] = request.Resolution.Replace("x", ":");

        if (request.Duration is not null)
            payload["duration"] = request.Duration;

        if (request.Seed is not null)
        {
            if (isImage || isVideo)
            {
                payload["seed"] = request.Seed;
            }
            else
            {
                warnings.Add(new { type = "unsupported", feature = "seed" });
            }
        }

        if (request.Image is not null)
        {
            var dataUri = request.Image.Data.ToDataUrl(request.Image.MediaType);

            if (isVideo)
            {
                payload["videoUri"] = dataUri;
            }
            else if (isImage)
            {
                payload["promptImage"] = dataUri;
            }
            else
            {
                throw new ArgumentException($"Unsupported mediaType '{request.Image.MediaType}'. Expected image/* or video/*.", nameof(request));
            }
        }

        return payload;
    }

    private static bool IsVideoUpscaleModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var localModel = model.Trim();
        var split = localModel.SplitModelId();
        if (string.Equals(split.Provider, "runway", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(split.Model))
        {
            localModel = split.Model;
        }

        return string.Equals(localModel, RunwayVideoUpscaleModel, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddVideoUpscaleUnsupportedWarnings(VideoRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Prompt))
            warnings.Add(new { type = "unsupported", feature = "prompt" });
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
    }

    private static Dictionary<string, object?> BuildVideoUpscalePayload(VideoRequest request, List<object> warnings)
    {
        var providerOptions = TryGetRunwayProviderOptions(request);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = RunwayVideoUpscaleModel
        };

        AddVideoUpscaleProviderOption(payload, providerOptions, "resolution");
        AddVideoUpscaleProviderOption(payload, providerOptions, "creativity");
        AddVideoUpscaleProviderOption(payload, providerOptions, "sharpen");
        AddVideoUpscaleProviderOption(payload, providerOptions, "smartGrain");
        AddVideoUpscaleProviderOption(payload, providerOptions, "flavor");
        AddVideoUpscaleProviderOption(payload, providerOptions, "fpsBoost");

        if (!payload.ContainsKey("resolution") && !string.IsNullOrWhiteSpace(request.Resolution))
            payload["resolution"] = request.Resolution;

        if (providerOptions is { ValueKind: JsonValueKind.Object })
        {
            foreach (var property in providerOptions.Value.EnumerateObject())
            {
                if (!IsSupportedVideoUpscaleProviderOption(property.Name))
                    warnings.Add(new { type = "unsupported", feature = $"providerOptions.runway.{property.Name}" });
            }
        }

        var inputReferences = request.InputReferences?.ToList() ?? [];
        var videoReference = inputReferences.FirstOrDefault(static r =>
            r.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));

        if (inputReferences.Any(static r => !r.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(new { type = "unsupported", feature = "inputReferences.nonVideo" });

        if (videoReference is null)
            throw new ArgumentException("Runway video upscale requires a video inputReference.", nameof(request));

        payload["videoUri"] = ToRunwayMediaUri(videoReference);

        return payload;
    }

    private static void AddVideoUpscaleProviderOption(Dictionary<string, object?> payload, JsonElement? providerOptions, string propertyName)
    {
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return;

        if (options.TryGetProperty(propertyName, out var property)
            && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            payload[propertyName] = property.Clone();
        }
    }

    private static bool IsSupportedVideoUpscaleProviderOption(string propertyName)
        => propertyName is "resolution" or "creativity" or "sharpen" or "smartGrain" or "flavor" or "fpsBoost";

    private static RunwayAvatarRoute ResolveAvatarRoute(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return RunwayAvatarRoute.NotAvatar;

        var localModel = model.Trim();
        var split = localModel.SplitModelId();
        if (string.Equals(split.Provider, "runway", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(split.Model))
        {
            localModel = split.Model;
        }

        if (string.Equals(localModel, RunwayAvatarModel, StringComparison.OrdinalIgnoreCase))
            return new RunwayAvatarRoute(true, null, null, true);

        const string prefix = RunwayAvatarModel + "/";
        if (!localModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return RunwayAvatarRoute.NotAvatar;

        var avatarId = localModel[prefix.Length..].Trim('/');
        if (string.IsNullOrWhiteSpace(avatarId))
            return new RunwayAvatarRoute(true, null, null, true);

        if (RunwayPresetAvatarIds.Contains(avatarId))
            return new RunwayAvatarRoute(true, avatarId, null, false);

        if (Guid.TryParse(avatarId, out _))
            return new RunwayAvatarRoute(true, null, avatarId, false);

        throw new ArgumentException($"Unsupported Runway avatar shortcut '{localModel}'. Use 'avatar', one of the preset avatar IDs, or 'avatar/{{custom_avatar_uuid}}'.", nameof(model));
    }

    private static void AddAvatarUnsupportedWarnings(VideoRequest request, List<object> warnings)
    {
        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });
        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });
        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
        if (request.FrameImages?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "frameImages" });
    }

    private static Dictionary<string, object?> BuildAvatarVideoPayload(
        VideoRequest request,
        RunwayAvatarRoute route,
        List<object> warnings)
    {
        var providerOptions = TryGetRunwayProviderOptions(request);
        var payload = providerOptions is { ValueKind: JsonValueKind.Object }
            ? JsonElementObjectToDictionary(providerOptions.Value)
            : [];

        payload["model"] = RunwayAvatarApiModel;

        if (route.IsGeneric)
        {
            if (!payload.ContainsKey("speech"))
                payload["speech"] = BuildAvatarSpeech(request, providerOptions, warnings);

            if (!payload.ContainsKey("avatar"))
                throw new ArgumentException("Runway generic avatar model requires providerOptions.runway.avatar matching the Runway /v1/avatar_videos schema.", nameof(request));

            return payload;
        }

        if (payload.ContainsKey("avatar"))
            warnings.Add(new { type = "ignored", feature = "providerOptions.runway.avatar", reason = "avatar shortcut models force the avatar from the model slug" });

        if (payload.ContainsKey("speech"))
            warnings.Add(new { type = "ignored", feature = "providerOptions.runway.speech", reason = "avatar shortcut models derive speech from inputReferences audio or prompt text" });

        payload["avatar"] = route.PresetId is not null
            ? new Dictionary<string, object?>
            {
                ["type"] = "runway-preset",
                ["presetId"] = route.PresetId
            }
            : new Dictionary<string, object?>
            {
                ["type"] = "custom",
                ["avatarId"] = route.CustomAvatarId
            };

        payload["speech"] = BuildAvatarSpeech(request, providerOptions, warnings);

        return payload;
    }

    private static object BuildAvatarSpeech(VideoRequest request, JsonElement? providerOptions, List<object> warnings)
    {
        var inputReferences = request.InputReferences?.ToList() ?? [];
        var audioReference = inputReferences.FirstOrDefault(static r =>
            r.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

        if (inputReferences.Any(static r => !r.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)))
            warnings.Add(new { type = "unsupported", feature = "inputReferences.nonAudio" });

        if (audioReference is not null)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = "audio",
                ["audio"] = ToRunwayMediaUri(audioReference)
            };
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Runway avatar video generation requires either prompt text or an audio inputReference.", nameof(request));

        var speech = new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = request.Prompt
        };

        var voice = TryGetAvatarVoice(providerOptions);
        if (voice is not null)
            speech["voice"] = voice.Value.Clone();

        return speech;
    }

    private static JsonElement? TryGetAvatarVoice(JsonElement? providerOptions)
    {
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return null;

        if (options.TryGetProperty("voice", out var voice) && voice.ValueKind == JsonValueKind.Object)
            return voice;

        if (options.TryGetProperty("speech", out var speech)
            && speech.ValueKind == JsonValueKind.Object
            && speech.TryGetProperty("voice", out var nestedVoice)
            && nestedVoice.ValueKind == JsonValueKind.Object)
        {
            return nestedVoice;
        }

        return null;
    }

    private static string ToRunwayMediaUri(VideoFile file)
    {
        var data = file.Data ?? string.Empty;
        return data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
               || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || data.StartsWith("runway://", StringComparison.OrdinalIgnoreCase)
            ? data
            : data.ToDataUrl(file.MediaType);
    }

    private static JsonElement? TryGetRunwayProviderOptions(VideoRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        return request.ProviderOptions.TryGetValue("runway", out var options) && options.ValueKind == JsonValueKind.Object
            ? options
            : null;
    }

    private static Dictionary<string, object?> JsonElementObjectToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in element.EnumerateObject())
            result[property.Name] = property.Value.Clone();

        return result;
    }

    private readonly record struct RunwayAvatarRoute(
        bool IsAvatar,
        string? PresetId,
        string? CustomAvatarId,
        bool IsGeneric)
    {
        public static RunwayAvatarRoute NotAvatar { get; } = new(false, null, null, false);
    }
}
