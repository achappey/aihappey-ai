using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.BytePlus;

/// <summary>
/// ProviderOptions schema for BytePlus (Seedance) video generation.
/// Consumed via <c>providerOptions.byteplus</c> for the unified video flow.
/// </summary>
public sealed class BytePlusVideoProviderMetadata
{
    /// <summary>
    /// Generate audio with the video (Seedance 1.5 pro only).
    /// </summary>
    [JsonPropertyName("generate_audio")]
    public bool? GenerateAudio { get; set; }

    /// <summary>
    /// Add watermark to the generated video.
    /// </summary>
    [JsonPropertyName("watermark")]
    public bool? Watermark { get; set; }

    /// <summary>
    /// Whether to fix the camera.
    /// </summary>
    [JsonPropertyName("camera_fixed")]
    public bool? CameraFixed { get; set; }

    /// <summary>
    /// Image roles for multi-image requests.
    /// </summary>
    [JsonPropertyName("image_roles")]
    public BytePlusVideoImageRoles? ImageRoles { get; set; }
}

public sealed class BytePlusVideoImageRoles
{
    /// <summary>
    /// Base64/data-url image to use as the first frame.
    /// </summary>
    [JsonPropertyName("first_frame")]
    public string? FirstFrame { get; set; }

    /// <summary>
    /// Base64/data-url image to use as the last frame.
    /// </summary>
    [JsonPropertyName("last_frame")]
    public string? LastFrame { get; set; }

    /// <summary>
    /// Base64/data-url reference images (1-4).
    /// </summary>
    [JsonPropertyName("reference_images")]
    public IReadOnlyList<string>? ReferenceImages { get; set; }
}
