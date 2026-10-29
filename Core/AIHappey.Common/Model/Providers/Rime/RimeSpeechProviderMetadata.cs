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

    [JsonPropertyName("timeScaleFactor")]
    public float? TimeScaleFactor { get; set; }

    /// <summary>Coda X-SAMPA pronunciations; supported for en, de, es, fr, it, and pt.</summary>
    [JsonPropertyName("customLexicon")]
    public RimePronunciationEntry[]? CustomLexicon { get; set; }

    [JsonPropertyName("noTextNormalization")]
    public bool? NoTextNormalization { get; set; }

    [JsonPropertyName("saveOovs")]
    public bool? SaveOovs { get; set; }
}

public sealed class RimePronunciationEntry
{
    [JsonPropertyName("spelling")]
    public string Spelling { get; set; } = null!;

    [JsonPropertyName("pronunciation")]
    public string Pronunciation { get; set; } = null!;
}
