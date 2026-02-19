using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.StepFun;

public sealed class StepFunSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("stream_format")]
    public string? StreamFormat { get; set; }

    [JsonPropertyName("voice_label")]
    public StepFunSpeechVoiceLabel? VoiceLabel { get; set; }

    [JsonPropertyName("pronunciation_map")]
    public StepFunSpeechPronunciationMap? PronunciationMap { get; set; }
}

public sealed class StepFunSpeechVoiceLabel
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }
}

public sealed class StepFunSpeechPronunciationMap
{
    [JsonPropertyName("tone")]
    public string[]? Tone { get; set; }
}

