using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraResembleAISpeechProviderMetadata
{
    /// <summary>
    /// Output format for the speech. Allowed values: mp3, opus, flac, wav, pcm.
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    /// <summary>
    /// Voice ID created on DeepInfra.
    /// </summary>
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Language code for multilingual models (e.g., en, fr, zh).
    /// </summary>
    [JsonPropertyName("language_id")]
    public string? LanguageId { get; set; }

    /// <summary>
    /// Exaggeration factor for the speech (0..1).
    /// </summary>
    [JsonPropertyName("exaggeration")]
    public double? Exaggeration { get; set; }

    /// <summary>
    /// CFG factor for the speech (0..1).
    /// </summary>
    [JsonPropertyName("cfg")]
    public double? Cfg { get; set; }

    /// <summary>
    /// Temperature for the speech (0..2).
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    /// <summary>
    /// Seed for the random number generator.
    /// </summary>
    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    /// <summary>
    /// Top P for the speech (0..1).
    /// </summary>
    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    /// <summary>
    /// Min P for the speech (0..1).
    /// </summary>
    [JsonPropertyName("min_p")]
    public double? MinP { get; set; }

    /// <summary>
    /// Repetition penalty for the speech (0..5).
    /// </summary>
    [JsonPropertyName("repetition_penalty")]
    public double? RepetitionPenalty { get; set; }

    /// <summary>
    /// Top K for the speech (0..1000).
    /// </summary>
    [JsonPropertyName("top_k")]
    public int? TopK { get; set; }

}
