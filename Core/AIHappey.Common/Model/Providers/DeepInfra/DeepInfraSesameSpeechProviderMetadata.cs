using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraSesameSpeechProviderMetadata
{
    /// <summary>
    /// Output format for the speech. Allowed values: mp3, opus, flac, wav, pcm.
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("preset_voice")]
    public string? PresetVoice { get; set; } //conversational_a conversational_b read_speech_a read_speech_b read_speech_c read_speech_d none

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("speaker_audio")]
    public string? SpeakerAudio { get; set; }

    [JsonPropertyName("speaker_transcript")]
    public string? SpeakerTranscript { get; set; }

    [JsonPropertyName("max_audio_length_ms")]
    public int? MaxAudioLengthMs { get; set; }
}
