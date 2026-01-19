using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.AIML;

/// <summary>
/// Provider-specific options for AIML audio generation models routed through the unified
/// <c>POST /v1/audio/speech</c> endpoint.
/// </summary>
public sealed class AIMLSpeechProviderMetadata
{
    [JsonPropertyName("stabilityai")]
    public AIMLSpeechStabilityAIProviderMetadata? StabilityAI { get; set; }

    [JsonPropertyName("minimax")]
    public AIMLSpeechMiniMaxProviderMetadata? MiniMax { get; set; }
}


public sealed class AIMLSpeechStabilityAIProviderMetadata
{
 
    /// <summary>
    /// Start point of the clip to generate.
    /// </summary>
    [JsonPropertyName("seconds_start")]
    public int? SecondsStart { get; set; }

    /// <summary>
    /// Total duration of the clip to generate.
    /// </summary>
    [JsonPropertyName("seconds_total")]
    public int? SecondsTotal { get; set; }

    /// <summary>
    /// Denoising steps.
    /// </summary>
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

}


public sealed class AIMLSpeechMiniMaxProviderMetadata
{
    /// <summary>
    /// Lyrics for song generation models (currently: <c>minimax/music-2.0</c>).
    /// Must be provided via <c>providerOptions.aiml.lyrics</c> (not via unified instructions).
    /// </summary>
    [JsonPropertyName("lyrics")]
    public string? Lyrics { get; set; }

    /// <summary>
    /// Model-specific advanced settings (pass-through object).
    /// </summary>
    [JsonPropertyName("audio_setting")]
    public JsonElement? AudioSetting { get; set; }
}

