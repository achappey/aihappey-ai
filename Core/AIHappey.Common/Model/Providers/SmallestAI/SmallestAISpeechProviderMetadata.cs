using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.SmallestAI;

/// <summary>
/// Provider options for SmallestAI TTS.
/// Consumed via <c>providerOptions.smallestai</c>.
/// </summary>
public sealed class SmallestAISpeechProviderMetadata
{
    /// <summary>
    /// Target sample rate. Supported values:
    /// - lightning-v3.1: 8000, 16000, 24000, 44100
    /// - lightning-v2: 8000..24000
    /// </summary>
    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    /// <summary>
    /// TTS language normalization code (e.g. auto, en, hi).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Output format (pcm, mp3, wav, mulaw).
    /// </summary>
    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Pronunciation dictionary IDs to apply.
    /// </summary>
    [JsonPropertyName("pronunciationDicts")]
    public List<string>? PronunciationDicts { get; set; }

    /// <summary>
    /// lightning-v2 only. Controls word repetition/skipping. Range 0..1.
    /// </summary>
    [JsonPropertyName("consistency")]
    public float? Consistency { get; set; }

    /// <summary>
    /// lightning-v2 only. Controls similarity to reference voice. Range 0..1.
    /// </summary>
    [JsonPropertyName("similarity")]
    public float? Similarity { get; set; }

    /// <summary>
    /// lightning-v2 only. Quality enhancement level. Range 0..2.
    /// </summary>
    [JsonPropertyName("enhancement")]
    public float? Enhancement { get; set; }
}

