using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Together;

public sealed class TogetherSpeechProviderMetadata
{
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("response_encoding")]
    public string? ResponseEncoding { get; set; } // pcm_f32le, pcm_s16le, pcm_mulaw, pcm_alaw

    [JsonPropertyName("language")]
    public string? Language { get; set; } //en, de, fr, es, hi, it, ja, ko, nl, pl, pt, ru, sv, tr, zh 

    [JsonPropertyName("cartesia")]
    public TogetherCartesiaSpeechProviderMetadata? Cartesia { get; set; }

    [JsonPropertyName("hexgrad")]
    public TogetherHexgradSpeechProviderMetadata? Hexgrad { get; set; }

    [JsonPropertyName("canopylabs")]
    public TogetherCanopyLabsSpeechProviderMetadata? CanopyLabs { get; set; }
}

public sealed class TogetherCartesiaSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}

public sealed class TogetherHexgradSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}

public sealed class TogetherCanopyLabsSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}
