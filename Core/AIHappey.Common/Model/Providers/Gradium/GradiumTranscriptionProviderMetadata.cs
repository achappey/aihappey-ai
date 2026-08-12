using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Gradium;

/// <summary>
/// Provider options for Gradium speech-to-text.
/// Consumed via <c>providerOptions.gradium</c>.
/// </summary>
public sealed class GradiumTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }
}
