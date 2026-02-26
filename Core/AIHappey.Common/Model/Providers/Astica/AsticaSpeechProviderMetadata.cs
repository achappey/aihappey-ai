using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Astica;

/// <summary>
/// Optional provider options for Astica Voice TTS.
/// Consumed via <c>providerOptions.astica</c>.
/// </summary>
public sealed class AsticaSpeechProviderMetadata
{
    /// <summary>
    /// Optional per-word timestamps in the response metadata (expressive voices only).
    /// </summary>
    [JsonPropertyName("timestamps")]
    public bool? Timestamps { get; set; }

    /// <summary>
    /// Optional style prompt used by programmable voices.
    /// </summary>
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }
}

