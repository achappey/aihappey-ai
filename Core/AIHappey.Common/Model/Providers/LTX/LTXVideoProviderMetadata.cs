using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.LTX;

/// <summary>
/// Provider-specific metadata for LTX video operations.
/// Consumed through <c>providerOptions.ltx</c> by the unified video endpoint.
/// </summary>
public sealed class LTXVideoProviderMetadata
{
    /// <summary>
    /// LTX operation to execute. Supported values: text-to-video, image-to-video,
    /// audio-to-video, retake, extend, video-to-video-hdr, hdr.
    /// </summary>
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    /// <summary>
    /// Provider-native model override. Useful when the public model id is a utility id
    /// such as ltx/video-to-video-hdr.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Prompt override for provider-specific flows.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    /// <summary>
    /// Image URI to use as image_uri. HTTPS URLs and provider storage URIs are passed through.
    /// </summary>
    [JsonPropertyName("image_uri")]
    public string? ImageUri { get; set; }

    /// <summary>
    /// Image payload to upload through LTX /v1/upload when image_uri is not directly available.
    /// Accepts a data URL or base64 payload.
    /// </summary>
    [JsonPropertyName("image_data")]
    public string? ImageData { get; set; }

    [JsonPropertyName("image_media_type")]
    public string? ImageMediaType { get; set; }

    /// <summary>
    /// Optional last frame URI. Supported by ltx-2-3 models.
    /// </summary>
    [JsonPropertyName("last_frame_uri")]
    public string? LastFrameUri { get; set; }

    /// <summary>
    /// Optional last-frame media payload to upload through LTX /v1/upload.
    /// </summary>
    [JsonPropertyName("last_frame_data")]
    public string? LastFrameData { get; set; }

    [JsonPropertyName("last_frame_media_type")]
    public string? LastFrameMediaType { get; set; }

    /// <summary>
    /// Audio URI for audio-to-video.
    /// </summary>
    [JsonPropertyName("audio_uri")]
    public string? AudioUri { get; set; }

    /// <summary>
    /// Audio payload to upload through LTX /v1/upload for audio-to-video.
    /// Accepts a data URL or base64 payload.
    /// </summary>
    [JsonPropertyName("audio_data")]
    public string? AudioData { get; set; }

    [JsonPropertyName("audio_media_type")]
    public string? AudioMediaType { get; set; }

    /// <summary>
    /// Video URI for retake, extend, or HDR operations.
    /// </summary>
    [JsonPropertyName("video_uri")]
    public string? VideoUri { get; set; }

    /// <summary>
    /// Video payload to upload through LTX /v1/upload for retake, extend, or HDR operations.
    /// Accepts a data URL or base64 payload.
    /// </summary>
    [JsonPropertyName("video_data")]
    public string? VideoData { get; set; }

    [JsonPropertyName("video_media_type")]
    public string? VideoMediaType { get; set; }

    /// <summary>
    /// Generate audio for text-to-video and image-to-video.
    /// </summary>
    [JsonPropertyName("generate_audio")]
    public bool? GenerateAudio { get; set; }

    /// <summary>
    /// Camera motion for text-to-video and image-to-video.
    /// </summary>
    [JsonPropertyName("camera_motion")]
    public string? CameraMotion { get; set; }

    /// <summary>
    /// Mode for retake or extend. Retake supports replace_audio, replace_video,
    /// replace_audio_and_video. Extend supports start or end.
    /// </summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    /// <summary>
    /// Start time for retake.
    /// </summary>
    [JsonPropertyName("start_time")]
    public double? StartTime { get; set; }

    /// <summary>
    /// Provider-specific duration override for operations accepting fractional seconds.
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Optional guidance scale for audio-to-video.
    /// </summary>
    [JsonPropertyName("guidance_scale")]
    public double? GuidanceScale { get; set; }

    /// <summary>
    /// Advanced context seconds for extend.
    /// </summary>
    [JsonPropertyName("context")]
    public double? Context { get; set; }

    /// <summary>
    /// Preferred result key to download for async HDR jobs, e.g. exr_frames_url.
    /// </summary>
    [JsonPropertyName("preferred_result_key")]
    public string? PreferredResultKey { get; set; }

    /// <summary>
    /// Poll interval in seconds for async HDR jobs. Defaults to 5.
    /// </summary>
    [JsonPropertyName("poll_interval_seconds")]
    public double? PollIntervalSeconds { get; set; }

    /// <summary>
    /// Poll timeout in seconds for async HDR jobs. Defaults to 600.
    /// </summary>
    [JsonPropertyName("timeout_seconds")]
    public double? TimeoutSeconds { get; set; }
}
