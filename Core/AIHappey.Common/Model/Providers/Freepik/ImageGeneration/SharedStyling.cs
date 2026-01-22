using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// Shared styling shape used by Freepik's Flux Dev / HyperFlux endpoints.
/// </summary>
public sealed class FreepikStyling
{
    [JsonPropertyName("effects")]
    public FreepikStylingEffects? Effects { get; set; }

    [JsonPropertyName("colors")]
    public List<FreepikStylingColor>? Colors { get; set; }
}

public sealed class FreepikStylingEffects
{
    [JsonPropertyName("color")]
    public string? Color { get; set; }

    [JsonPropertyName("framing")]
    public string? Framing { get; set; }

    [JsonPropertyName("lightning")]
    public string? Lightning { get; set; }
}

public sealed class FreepikStylingColor
{
    [JsonPropertyName("color")]
    public string Color { get; set; } = null!;

    [JsonPropertyName("weight")]
    public float Weight { get; set; }
}

