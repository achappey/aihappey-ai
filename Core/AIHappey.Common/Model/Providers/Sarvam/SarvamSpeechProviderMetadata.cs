using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Sarvam;

/// <summary>
/// Provider-specific options for Sarvam Text-to-Speech.
/// Matches Sarvam REST endpoint <c>POST /text-to-speech</c> fields.
/// </summary>
public sealed class SarvamSpeechProviderMetadata
{
    [JsonPropertyName("target_language_code")]
    public string? TargetLanguageCode { get; set; }

    /// <summary>
    /// Sarvam speaker name (voice). Default on Sarvam side is typically "Anushka".
    /// </summary>
    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }

    [JsonPropertyName("pitch")]
    public double? Pitch { get; set; }

    [JsonPropertyName("pace")]
    public double? Pace { get; set; }

    [JsonPropertyName("loudness")]
    public double? Loudness { get; set; }

    [JsonPropertyName("speech_sample_rate")]
    public int? SpeechSampleRate { get; set; }

    [JsonPropertyName("enable_preprocessing")]
    public bool? EnablePreprocessing { get; set; }

    /// <summary>
    /// Sarvam model id/version (e.g. "bulbul:v2").
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Sarvam output codec (e.g. wav, mp3, flac, opus, aac, linear16, mulaw, alaw).
    /// </summary>
    [JsonPropertyName("output_audio_codec")]
    public string? OutputAudioCodec { get; set; }
}

