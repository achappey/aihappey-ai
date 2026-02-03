using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.KlingAI;

/// <summary>
/// ProviderOptions schema for KlingAI text-to-audio.
/// Consumed via <c>providerOptions.klingai</c>.
/// </summary>
public sealed class KlingAISpeechProviderMetadata
{
    /// <summary>
    /// Generated audio duration in seconds (3.0..10.0).
    /// </summary>
    [JsonPropertyName("duration")]
    public float? Duration { get; set; }

    /// <summary>
    /// Optional external task id for status queries.
    /// </summary>
    [JsonPropertyName("external_task_id")]
    public string? ExternalTaskId { get; set; }

    /// <summary>
    /// Optional callback URL for task status notifications.
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }
}
