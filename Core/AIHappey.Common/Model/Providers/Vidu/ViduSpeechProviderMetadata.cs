using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Vidu;

public sealed class ViduSpeechProviderMetadata
{
    /// <summary>
    /// Audio duration for text-to-audio (seconds). Range: 2-10.
    /// </summary>
    [JsonPropertyName("duration")]
    public float? Duration { get; set; }

    /// <summary>
    /// Random seed for text-to-audio.
    /// </summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>
    /// Callback URL for task status changes.
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }

    /// <summary>
    /// Transparent transmission payload.
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    /// <summary>
    /// Voice ID for text-to-speech.
    /// </summary>
    [JsonPropertyName("voice_setting_voice_id")]
    public string? VoiceSettingVoiceId { get; set; }

    /// <summary>
    /// Speech speed for text-to-speech (0.5-2.0).
    /// </summary>
    [JsonPropertyName("voice_setting_speed")]
    public float? VoiceSettingSpeed { get; set; }

    /// <summary>
    /// Volume for text-to-speech (0-10).
    /// </summary>
    [JsonPropertyName("voice_setting_volume")]
    public int? VoiceSettingVolume { get; set; }

    /// <summary>
    /// Pitch for text-to-speech (-12 to 12).
    /// </summary>
    [JsonPropertyName("voice_setting_pitch")]
    public int? VoiceSettingPitch { get; set; }

    /// <summary>
    /// Emotion for text-to-speech.
    /// </summary>
    [JsonPropertyName("voice_setting_emotion")]
    public string? VoiceSettingEmotion { get; set; }
}
