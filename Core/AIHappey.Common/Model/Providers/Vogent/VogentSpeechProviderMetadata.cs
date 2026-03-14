using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Vogent;

public sealed class VogentSpeechProviderMetadata
{
    [JsonPropertyName("voiceId")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("outputType")]
    public string? OutputType { get; set; }

    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("voiceOptionValues")]
    public List<VogentSpeechOptionValue>? VoiceOptionValues { get; set; }
}

public sealed class VogentSpeechOptionValue
{
    [JsonPropertyName("optionId")]
    public string OptionId { get; set; } = null!;

    [JsonPropertyName("value")]
    public string Value { get; set; } = null!;
}
