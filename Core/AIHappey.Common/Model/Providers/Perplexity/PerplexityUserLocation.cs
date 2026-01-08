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
}

