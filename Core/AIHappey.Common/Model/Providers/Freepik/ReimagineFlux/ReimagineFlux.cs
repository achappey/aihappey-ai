using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class ReimagineFlux
{
    /// <summary>
    /// Imagination type.
    /// </summary>
    /// <remarks>Allowed values: wild, subtle, vivid.</remarks>
    [JsonPropertyName("imagination")]
    public string? Imagination { get; set; }

    /// <summary>
    /// Output aspect ratio.
    /// </summary>
    /// <remarks>
    /// Allowed values: original, square_1_1, classic_4_3, traditional_3_4, widescreen_16_9,
    /// social_story_9_16, standard_3_2, portrait_2_3, horizontal_2_1, vertical_1_2, social_post_4_5.
    /// </remarks>
    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }
}

