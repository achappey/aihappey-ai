using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

public sealed class VerdaFlux2ImageProviderMetadata
{
    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("guidance")]
    public float? Guidance { get; set; }
}
