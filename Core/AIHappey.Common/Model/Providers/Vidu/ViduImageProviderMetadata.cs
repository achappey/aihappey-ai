using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Vidu;

/// <summary>
/// ProviderOptions schema for Vidu image generation.
/// Consumed via <c>providerOptions.vidu</c> for the unified image flow.
/// </summary>
public sealed class ViduImageProviderMetadata
{
    /// <summary>
    /// Output resolution (e.g. 1080p, 2K, 4K).
    /// </summary>
    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    /// <summary>
    /// Transparent transmission payload.
    /// </summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }

    /// <summary>
    /// Callback URL for task status changes.
    /// </summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }
}
