using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.APIpie;

/// <summary>
/// Provider options for APIpie image generation.
/// Consumed via <c>providerOptions.apipie</c>.
/// </summary>
public sealed class APIpieImageProviderMetadata
{
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("loras")]
    public IEnumerable<string>? Loras { get; set; }

    [JsonPropertyName("strength")]
    public double? Strength { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("text_layout")]
    public IEnumerable<APIpieTextLayoutItem>? TextLayout { get; set; }
}

public sealed class APIpieTextLayoutItem
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("bbox")]
    public IEnumerable<JsonElement>? Bbox { get; set; }
}
