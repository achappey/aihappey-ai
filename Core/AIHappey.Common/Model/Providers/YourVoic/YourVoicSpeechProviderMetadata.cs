using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.YourVoic;

/// <summary>
/// Provider-specific options for YourVoic Text-to-Speech.
/// </summary>
public sealed class YourVoicSpeechProviderMetadata
{
    /// <summary>
    /// Voice pitch multiplier. Range typically 0.5 to 2.0.
    /// </summary>
    [JsonPropertyName("pitch")]
    public float? Pitch { get; set; }

    /// <summary>
    /// Output format override (mp3, wav).
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

