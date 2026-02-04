using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Alibaba;

/// <summary>
/// ProviderOptions schema for Alibaba (DashScope) transcription.
/// Consumed via <c>providerOptions.alibaba</c>.
/// </summary>
public sealed class AlibabaTranscriptionProviderMetadata
{
    /// <summary>
    /// Optional language hint for recognition (single language).
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Enable inverse text normalization (Chinese/English only).
    /// </summary>
    [JsonPropertyName("enable_itn")]
    public bool? EnableItn { get; set; }
}
