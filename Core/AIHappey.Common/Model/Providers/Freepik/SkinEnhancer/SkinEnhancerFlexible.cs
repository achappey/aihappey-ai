using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class SkinEnhancerFlexible : SkinEnhancerBase
{
    /// <summary>
    /// Optimization target for flexible skin enhancer.
    /// </summary>
    /// <remarks>Allowed values: enhance_skin, improve_lighting, enhance_everything, transform_to_real, no_make_up.</remarks>
    [JsonPropertyName("optimized_for")]
    public string? OptimizedFor { get; set; }
}

