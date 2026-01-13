using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Speechify;

/// <summary>
/// Provider-specific options for Speechify Text-to-Speech.
/// Maps to <c>POST https://api.sws.speechify.com/v1/audio/speech</c>.
/// </summary>
public sealed class SpeechifySpeechProviderMetadata
{
    /// <summary>
    /// Speechify voice id (required by Speechify).
    /// </summary>
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    /// <summary>
    /// Output audio format. Allowed values (per docs): wav, mp3, ogg, aac, pcm.
    /// </summary>
    [JsonPropertyName("audio_format")]
    public string? AudioFormat { get; set; }

    /// <summary>
    /// Language of the input (IETF tag like en-US).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Speechify options wrapper.
    /// Included as the top-level "options" property in the request body.
    /// </summary>
    [JsonPropertyName("options")]
    public SpeechifySpeechOptions? Options { get; set; }
}

/// <summary>
/// Speechify request options wrapper for <c>POST /v1/audio/speech</c>.
/// </summary>
public sealed class SpeechifySpeechOptions
{
    /// <summary>
    /// Determines whether to normalize the audio loudness to a standard level.
    /// Defaults to false (Speechify-side default).
    /// </summary>
    [JsonPropertyName("loudness_normalization")]
    public bool? LoudnessNormalization { get; set; }

    /// <summary>
    /// Determines whether to normalize the text (numbers/dates to words).
    /// Defaults to true (Speechify-side default).
    /// </summary>
    [JsonPropertyName("text_normalization")]
    public bool? TextNormalization { get; set; }
}
