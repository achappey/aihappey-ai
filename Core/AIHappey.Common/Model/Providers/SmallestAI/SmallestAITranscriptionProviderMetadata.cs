using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.SmallestAI;

/// <summary>
/// Provider options for SmallestAI Pulse and Pulse Pro (pre-recorded) STT.
/// Consumed via <c>providerOptions.smallestai</c>.
/// </summary>
public sealed class SmallestAITranscriptionProviderMetadata
{
    /// <summary>
    /// Language code accepted by the selected model. Pulse Pro supports <c>en</c> only.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("wordTimestamps")]
    public bool? WordTimestamps { get; set; }

    [JsonPropertyName("diarize")]
    public bool? Diarize { get; set; }

    [JsonPropertyName("genderDetection")]
    public bool? GenderDetection { get; set; }

    [JsonPropertyName("emotionDetection")]
    public bool? EmotionDetection { get; set; }

    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; set; }

    [JsonPropertyName("webhookMethod")]
    public string? WebhookMethod { get; set; }

    [JsonPropertyName("webhookExtra")]
    public string? WebhookExtra { get; set; }

    [JsonPropertyName("redactPii")]
    public bool? RedactPii { get; set; }

    [JsonPropertyName("redactPci")]
    public bool? RedactPci { get; set; }
}

