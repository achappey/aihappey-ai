using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Sarvam;

/// <summary>
/// Provider-specific options for Sarvam Speech-to-Text.
/// Matches Sarvam REST endpoint <c>POST /speech-to-text</c> fields.
/// </summary>
public sealed class SarvamTranscriptionProviderMetadata
{
    /// <summary>
    /// Sarvam model id/version. Default on Sarvam side is <c>saarika:v2.5</c>.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// BCP-47 language code (or <c>unknown</c> for auto-detect).
    /// For <c>saarika:v2.5</c> this is optional.
    /// </summary>
    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    /// <summary>
    /// Input audio codec/format (required for raw PCM variants).
    /// Example values: wav, mp3, opus, flac, mp4, pcm_s16le, pcm_l16, pcm_raw.
    /// </summary>
    [JsonPropertyName("input_audio_codec")]
    public string? InputAudioCodec { get; set; }

    /// <summary>
    /// If true, Sarvam includes word-level timestamps in the response.
    /// </summary>
    [JsonPropertyName("with_timestamps")]
    public bool? WithTimestamps { get; set; }
}

