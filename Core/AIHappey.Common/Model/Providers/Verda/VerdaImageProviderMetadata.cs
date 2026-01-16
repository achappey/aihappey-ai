using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Verda;

//https://docs.verda.com/inference/image-models
public sealed class VerdaImageProviderMetadata
{
    [JsonPropertyName("flux_1")]
    public VerdaFlux1ImageProviderMetadata? Flux1 { get; set; }

    [JsonPropertyName("flux_2")]
    public VerdaFlux2ImageProviderMetadata? Flux2 { get; set; }

    [JsonPropertyName("flux_2_klein")]
    public VerdaFlux2KleinImageProviderMetadata? Flux2Klein { get; set; }


}
