using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.UnrealSpeech;

/// <summary>
/// Provider options for UnrealSpeech TTS.
/// Consumed via <c>providerOptions.unrealspeech</c>.
/// </summary>
public sealed class UnrealSpeechSpeechProviderMetadata
{
    [JsonPropertyName("bitrate")]
    public string? Bitrate { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("pitch")]
    public float? Pitch { get; set; }

    [JsonPropertyName("timestampType")]
    public string? TimestampType { get; set; }

    [JsonPropertyName("codec")]
    public string? Codec { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}

