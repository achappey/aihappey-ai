using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Fireworks;

public class FireworksTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }

    [JsonPropertyName("vad_model")]
    public string? VadModel { get; set; }

    [JsonPropertyName("alignment_model")]
    public string? AlignmentModel { get; set; }

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("min_speakers")]
    public int? MinSpeakers { get; set; }

    [JsonPropertyName("max_speakers")]
    public int? MaxSpeakers { get; set; }

    [JsonPropertyName("preprocessing")]
    public string? Preprocessing { get; set; }
}

