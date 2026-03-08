using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Rime;

/// <summary>
/// Provider options for Rime TTS.
/// Consumed via <c>providerOptions.rime</c>.
/// </summary>
public sealed class RimeSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("pauseBetweenBrackets")]
    public bool? PauseBetweenBrackets { get; set; }

    [JsonPropertyName("phonemizeBetweenBrackets")]
    public bool? PhonemizeBetweenBrackets { get; set; }

    [JsonPropertyName("inlineSpeedAlpha")]
    public string? InlineSpeedAlpha { get; set; }

    [JsonPropertyName("samplingRate")]
    public int? SamplingRate { get; set; }

    [JsonPropertyName("speedAlpha")]
    public float? SpeedAlpha { get; set; }

    [JsonPropertyName("noTextNormalization")]
    public bool? NoTextNormalization { get; set; }

    [JsonPropertyName("saveOovs")]
    public bool? SaveOovs { get; set; }
}
