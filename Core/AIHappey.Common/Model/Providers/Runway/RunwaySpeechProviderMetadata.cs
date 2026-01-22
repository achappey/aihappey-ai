using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runway;

/// <summary>
/// Provider-specific options for Runway Text-to-Speech.
/// Maps to <c>POST /v1/text_to_speech</c>.
/// </summary>
public sealed class RunwaySpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public RunwaySpeechVoicePreset? Voice { get; set; }

    /// <summary>
    /// Provider-specific options for Runway Sound Effects.
    /// Maps to <c>POST /v1/sound_effect</c>.
    /// </summary>
    [JsonPropertyName("soundEffects")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunwaySoundEffectsConfig? SoundEffects { get; set; }
}


/// <summary>
/// Provider-specific options for Runway Text-to-Speech.
/// Maps to <c>POST /v1/text_to_speech</c>.
/// </summary>
public sealed class RunwaySpeechVoicePreset
{

    [JsonPropertyName("type")]
    public string Type { get; set; } = "runway-preset";

    [JsonPropertyName("presetId")]
    public string PresetId { get; set; } = null!;
}

/// <summary>
/// Provider-specific options for Runway Sound Effects.
/// Maps to <c>POST /v1/sound_effect</c>.
/// </summary>
public sealed class RunwaySoundEffectsConfig
{
    /// <summary>
    /// Duration of the sound effect in seconds.
    /// Runway docs: 0.5..30. If null, Runway chooses an appropriate duration.
    /// </summary>
    [JsonPropertyName("duration")]
    public double? Duration { get; set; }

    /// <summary>
    /// Whether the output sound effect should be designed to loop seamlessly.
    /// Defaults to false when omitted.
    /// </summary>
    [JsonPropertyName("loop")]
    public bool? Loop { get; set; }
}
