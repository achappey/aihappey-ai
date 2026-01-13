using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.MiniMax;

/// <summary>
/// ProviderOptions schema for MiniMax speech synthesis (Text-to-Audio).
/// Consumed via <c>providerOptions.minimax</c> for the unified <c>/v1/audio/speech</c> flow.
/// </summary>
public sealed class MiniMaxSpeechProviderMetadata
{
    [JsonPropertyName("voice_setting")]
    public MiniMaxVoiceSetting? VoiceSetting { get; set; }

    [JsonPropertyName("audio_setting")]
    public MiniMaxAudioSetting? AudioSetting { get; set; }

    [JsonPropertyName("pronunciation_dict")]
    public MiniMaxPronunciationDict? PronunciationDict { get; set; }

    [JsonPropertyName("language_boost")]
    public string? LanguageBoost { get; set; }

    [JsonPropertyName("voice_modify")]
    public MiniMaxVoiceModify? VoiceModify { get; set; }

    [JsonPropertyName("subtitle_enable")]
    public bool? SubtitleEnable { get; set; }

    [JsonPropertyName("lyrics")]
    public string? Lyrics { get; set; }
}

public sealed class MiniMaxVoiceSetting
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("vol")]
    public double? Vol { get; set; }

    [JsonPropertyName("pitch")]
    public int? Pitch { get; set; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("text_normalization")]
    public bool? TextNormalization { get; set; }

    [JsonPropertyName("latex_read")]
    public bool? LatexRead { get; set; }
}

public sealed class MiniMaxAudioSetting
{
    [JsonPropertyName("sample_rate")]
    public long? SampleRate { get; set; }

    [JsonPropertyName("bitrate")]
    public long? Bitrate { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("channel")]
    public long? Channel { get; set; }

    [JsonPropertyName("force_cbr")]
    public bool? ForceCbr { get; set; }
}

public sealed class MiniMaxPronunciationDict
{
    [JsonPropertyName("tone")]
    public string[]? Tone { get; set; }
}

public sealed class MiniMaxTimbreWeight
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("weight")]
    public long? Weight { get; set; }
}

public sealed class MiniMaxVoiceModify
{
    [JsonPropertyName("pitch")]
    public int? Pitch { get; set; }

    [JsonPropertyName("intensity")]
    public int? Intensity { get; set; }

    [JsonPropertyName("timbre")]
    public int? Timbre { get; set; }

    [JsonPropertyName("sound_effects")]
    public string? SoundEffects { get; set; }
}
