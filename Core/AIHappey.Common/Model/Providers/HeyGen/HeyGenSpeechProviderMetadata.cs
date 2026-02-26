using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.HeyGen;

/// <summary>
/// Provider options for HeyGen Starfish TTS.
/// Consumed via <c>providerOptions.heygen</c>.
/// </summary>
public sealed class HeyGenSpeechProviderMetadata
{
    /// <summary>
    /// Optional input type. Allowed values: <c>text</c>, <c>ssml</c>.
    /// </summary>
    [JsonPropertyName("inputType")]
    public string? InputType { get; set; }

    /// <summary>
    /// Optional speed multiplier (0.5 - 2.0).
    /// </summary>
    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    /// <summary>
    /// Optional base language code (e.g. en, pt, zh).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Optional locale code (e.g. en-US, pt-BR).
    /// </summary>
    [JsonPropertyName("locale")]
    public string? Locale { get; set; }
}

