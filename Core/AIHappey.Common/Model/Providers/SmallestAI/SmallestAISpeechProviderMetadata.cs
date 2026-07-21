using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.SmallestAI;

/// <summary>
/// Provider options for SmallestAI TTS.
/// Consumed via <c>providerOptions.smallestai</c>.
/// </summary>
public sealed class SmallestAISpeechProviderMetadata
{
    /// <summary>
    /// Target sample rate. Supported values are 8000, 16000, 24000, and 44100.
    /// </summary>
    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    /// <summary>
    /// TTS language normalization code (e.g. auto, en, hi).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Output format (pcm, mp3, wav, ulaw, or alaw).
    /// </summary>
    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Pronunciation dictionary IDs to apply.
    /// </summary>
    [JsonPropertyName("pronunciationDicts")]
    public List<string>? PronunciationDicts { get; set; }

    /// <summary>
    /// Optional language used to normalize numeric content independently of
    /// <see cref="Language"/>.
    /// </summary>
    [JsonPropertyName("numberPronunciationLanguage")]
    public string? NumberPronunciationLanguage { get; set; }

    /// <summary>
    /// Optional client session correlation identifier. It is echoed by
    /// SmallestAI in the <c>X-External-Session-Id</c> response header.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    /// <summary>
    /// Optional client request correlation identifier. It is echoed by
    /// SmallestAI in the <c>X-External-Request-Id</c> response header.
    /// </summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    /// <summary>
    /// Requests word timestamps when using a WebSocket session. This is
    /// accepted but ignored by the HTTP sync and SSE endpoints.
    /// </summary>
    [JsonPropertyName("wordTimestamps")]
    public bool? WordTimestamps { get; set; }
}

