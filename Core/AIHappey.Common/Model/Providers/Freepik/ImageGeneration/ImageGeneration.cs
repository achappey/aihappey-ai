using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class ImageGeneration
{
    [JsonPropertyName("fluxdev")]
    public FluxDev? FluxDev { get; set; }

    [JsonPropertyName("fluxprov11")]
    public FluxProV11? FluxProV11 { get; set; }

    [JsonPropertyName("hyperflux")]
    public Hyperflux? Hyperflux { get; set; }

    [JsonPropertyName("seedream")]
    public Seedream? Seedream { get; set; }

    [JsonPropertyName("seedreamv4")]
    public SeedreamV4? SeedreamV4 { get; set; }

    [JsonPropertyName("seedreamv4edit")]
    public SeedreamV4Edit? SeedreamV4Edit { get; set; }

    [JsonPropertyName("seedreamv45")]
    public SeedreamV45? SeedreamV45 { get; set; }

    [JsonPropertyName("zimageturbo_model")]
    public ZImageTurboModel? ZImageTurboModel { get; set; }

    [JsonPropertyName("flux2")]
    public Flux2? Flux2 { get; set; }

    [JsonPropertyName("flux2turbo")]
    public Flux2Turbo? Flux2Turbo { get; set; }

    [JsonPropertyName("classicfast")]
    public ClassicFast? ClassicFast { get; set; }

    [JsonPropertyName("seedream45edit")]
    public Seedream45Edit? Seedream45Edit { get; set; }

    [JsonPropertyName("zimageturbo")]
    public ZImageTurbo? ZImageTurbo { get; set; }

    [JsonPropertyName("mystic")]
    public Mystic? Mystic { get; set; }

}

