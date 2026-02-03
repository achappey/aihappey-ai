using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Combined providerOptions schema for Runware (image + video inference).
/// </summary>
public sealed class RunwareProviderMetadata : RunwareImageProviderMetadata
{
    [JsonPropertyName("webhookURL")]
    public string? WebhookUrl { get; set; }

    [JsonPropertyName("uploadEndpoint")]
    public string? UploadEndpoint { get; set; }

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    [JsonPropertyName("numberResults")]
    public int? NumberResults { get; set; }

    [JsonPropertyName("acceleration")]
    public string? Acceleration { get; set; }

    [JsonPropertyName("advancedFeatures")]
    public JsonElement? AdvancedFeatures { get; set; }

    [JsonPropertyName("frameImages")]
    public IReadOnlyList<RunwareVideoFrameImage>? FrameImages { get; set; }

    [JsonPropertyName("referenceImages")]
    public IReadOnlyList<string>? ReferenceImages { get; set; }

    [JsonPropertyName("referenceVideos")]
    public IReadOnlyList<string>? ReferenceVideos { get; set; }

    [JsonPropertyName("inputAudios")]
    public IReadOnlyList<string>? InputAudios { get; set; }

    [JsonPropertyName("speech")]
    public RunwareVideoSpeech? Speech { get; set; }
}
