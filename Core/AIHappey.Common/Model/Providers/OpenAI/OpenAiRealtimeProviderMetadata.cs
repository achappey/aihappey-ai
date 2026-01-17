using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiRealtimeProviderMetadata
{
    [JsonPropertyName("expires_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiRealtimeExpiresAfter? ExpiresAfter { get; set; }

    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealtimeTranscriptionSession? Session { get; set; }
}

public sealed class OpenAiRealtimeExpiresAfter
{
    [JsonPropertyName("anchor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Anchor { get; set; }

    [JsonPropertyName("seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seconds { get; set; }

}

public sealed class RealtimeTranscriptionSession
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "transcription";

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RealtimeAudioConfig? Audio { get; set; }

    [JsonPropertyName("include")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Include { get; set; }
}


#region Audio

public sealed class RealtimeAudioConfig
{
    [JsonPropertyName("input")]
    public RealtimeAudioInputConfig? Input { get; set; }
}

public sealed class RealtimeAudioInputConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("format")]
    public AudioFormat? Format { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("noise_reduction")]
    public NoiseReductionConfig? NoiseReduction { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("transcription")]
    public RealtimeTranscriptionConfig? Transcription { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("turn_detection")]
    public TurnDetectionConfig? TurnDetection { get; set; }
}

#endregion


#region Audio Formats (union)

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PcmAudioFormat), "audio/pcm")]
[JsonDerivedType(typeof(PcmuAudioFormat), "audio/pcmu")]
[JsonDerivedType(typeof(PcmaAudioFormat), "audio/pcma")]
public abstract class AudioFormat
{
    //    [JsonPropertyName("type")]
    //  public required string Type { get; init; }
}

public sealed class PcmAudioFormat : AudioFormat
{
    [JsonPropertyName("rate")]
    public int? Rate { get; set; } = 24000;

}

public sealed class PcmuAudioFormat : AudioFormat
{

}

public sealed class PcmaAudioFormat : AudioFormat
{

}

#endregion

#region Noise Reduction

public sealed class NoiseReductionConfig
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } // near_field | far_field
}

#endregion

#region Transcription

public sealed class RealtimeTranscriptionConfig
{
    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Prompt { get; set; }
}

#endregion


[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ServerVadConfig), "server_vad")]
[JsonDerivedType(typeof(SemanticVadConfig), "semantic_vad")]
public abstract class TurnDetectionConfig
{
    [JsonPropertyName("create_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CreateResponse { get; set; }

    [JsonPropertyName("interrupt_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? InterruptResponse { get; set; }
}

public sealed class ServerVadConfig : TurnDetectionConfig
{
    [JsonPropertyName("idle_timeout_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IdleTimeoutMs { get; set; }

    [JsonPropertyName("prefix_padding_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PrefixPaddingMs { get; set; }

    [JsonPropertyName("silence_duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SilenceDurationMs { get; set; }

    [JsonPropertyName("threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Threshold { get; set; }


}

public sealed class SemanticVadConfig : TurnDetectionConfig
{
    [JsonPropertyName("eagerness")]
    public string? Eagerness { get; set; } // low | medium | high | auto

}