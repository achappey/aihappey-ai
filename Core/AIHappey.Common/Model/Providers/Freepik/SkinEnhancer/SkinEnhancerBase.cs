using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public class SkinEnhancerBase
{
    /// <summary>
    /// Sharpening intensity.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("sharpen")]
    public int? Sharpen { get; set; }

    /// <summary>
    /// Smart grain intensity.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("smart_grain")]
    public int? SmartGrain { get; set; }
}

