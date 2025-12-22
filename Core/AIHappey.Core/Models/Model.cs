
using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public class Model
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long? Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    //  [JsonPropertyName("publisher")]
    //   public string Publisher { get; set; } = default!;

    // [JsonPropertyName("provider")]
    //  public string Provider { get; set; } = default!;

    [JsonPropertyName("context_window")]
    public int? ContextWindow { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("tags")]
    public IEnumerable<string>? Tags { get; set; }

    [JsonPropertyName("pricing")]
    public ModelPricing? Pricing { get; set; }
}

public class ModelPricing
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = default!;

    [JsonPropertyName("output")]
    public string Output { get; set; } = default!;

    [JsonPropertyName("input_cache_read")]
    public string? InputCacheRead { get; set; }

    [JsonPropertyName("input_cache_write")]
    public string? InputCacheWrite { get; set; }
}

public class ModelReponse
{
    [JsonPropertyName("object")]
    public string Object { get; } = "list";

    [JsonPropertyName("data")]
    public IEnumerable<Model> Data { get; set; } = [];
}
