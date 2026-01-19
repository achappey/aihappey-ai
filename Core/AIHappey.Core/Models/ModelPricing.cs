
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public class ModelPricing
{
    [JsonPropertyName("input")]
    [JsonConverter(typeof(TokenPriceConverter))]
    public decimal Input { get; set; } = default!;

    [JsonPropertyName("output")]
    [JsonConverter(typeof(TokenPriceConverter))]
    public decimal Output { get; set; } = default!;

    [JsonPropertyName("input_cache_read")]
    [JsonConverter(typeof(TokenPriceConverter))]
    public decimal? InputCacheRead { get; set; }

    [JsonPropertyName("input_cache_write")]
    [JsonConverter(typeof(TokenPriceConverter))]
    public decimal? InputCacheWrite { get; set; }
}