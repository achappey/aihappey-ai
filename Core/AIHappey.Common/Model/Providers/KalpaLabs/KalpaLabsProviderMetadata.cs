using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.KalpaLabs;

public sealed class KalpaLabsProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("params")]
    public KalpaLabsGenerationParams? Params { get; set; }

    [JsonPropertyName("conversation")]
    public List<KalpaLabsTurn>? Conversation { get; set; }

    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }
}

public sealed class KalpaLabsGenerationParams
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("acoustic_temperature")]
    public double? AcousticTemperature { get; set; }

    [JsonPropertyName("audio_quality")]
    public string? AudioQuality { get; set; }
}

public sealed class KalpaLabsTurn
{
    [JsonPropertyName("speaker")]
    public string Speaker { get; set; } = "0";

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("audio_wav_b64")]
    public string? AudioWavBase64 { get; set; }
}
