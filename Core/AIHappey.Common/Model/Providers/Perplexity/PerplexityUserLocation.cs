using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class PerplexityUserLocation
{
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

}

