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

    [JsonPropertyName("lines")]
    public List<VogentMultispeakerLine>? Lines { get; set; }
}

public sealed class VogentMultispeakerLine
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = null!;

    [JsonPropertyName("voiceId")]
    public string VoiceId { get; set; } = null!;
}

public sealed class VogentSpeechOptionValue
{
    [JsonPropertyName("optionId")]
    public string OptionId { get; set; } = null!;

    [JsonPropertyName("value")]
    public string Value { get; set; } = null!;
}
