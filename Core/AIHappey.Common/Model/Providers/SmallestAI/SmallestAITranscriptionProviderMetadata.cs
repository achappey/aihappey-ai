using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.SmallestAI;

/// <summary>
/// Provider options for SmallestAI Pulse (pre-recorded) STT.
/// Consumed via <c>providerOptions.smallestai</c>.
/// </summary>
public sealed class SmallestAITranscriptionProviderMetadata
{
    /// <summary>
    /// Language code (ISO-639-1) or <c>multi</c> for auto detection.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("wordTimestamps")]
    public bool? WordTimestamps { get; set; }

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("ageDetection")]
    public bool? AgeDetection { get; set; }

    [JsonPropertyName("genderDetection")]
    public bool? GenderDetection { get; set; }

    [JsonPropertyName("emotionDetection")]
    public bool? EmotionDetection { get; set; }

    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; set; }

    [JsonPropertyName("webhookExtra")]
    public string? WebhookExtra { get; set; }
}

