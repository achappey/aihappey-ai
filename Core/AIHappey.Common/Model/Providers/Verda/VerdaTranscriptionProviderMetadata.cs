using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

public sealed class VerdaTranscriptionProviderMetadata
{
    /// <summary>
    /// Audio file URL for Verda Whisper endpoint. If omitted, provider falls back to request.Audio
    /// (URL or data URL constructed from base64 + mediaType).
    /// </summary>
    [JsonPropertyName("audio_input")]
    public string? AudioInput { get; set; }

    /// <summary>
    /// Translate transcription result to English.
    /// </summary>
    [JsonPropertyName("translate")]
    public bool? Translate { get; set; }

    /// <summary>
    /// Optional 2-letter language code.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Whisper processing mode. Supported by Verda docs: diarize, align.
    /// </summary>
    [JsonPropertyName("processing_type")]
    public string? ProcessingType { get; set; }

    /// <summary>
    /// Output format. Supported by Verda docs: subtitles, raw.
    /// </summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }

    
}

