using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cartesia;

/// <summary>
/// Provider options for Cartesia STT.
/// Consumed via <c>providerOptions.cartesia</c>.
/// </summary>
public sealed class CartesiaTranscriptionProviderMetadata
{
    [JsonPropertyName("apiVersion")]
    public string? ApiVersion { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }

    [JsonPropertyName("sampleRate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("timestampGranularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

