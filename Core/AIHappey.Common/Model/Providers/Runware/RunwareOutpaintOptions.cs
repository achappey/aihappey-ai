using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware outpainting directions object.
/// Used by BFL FLUX.1 Expand Pro (<c>bfl:1@3</c>) via the top-level <c>outpaint</c> payload property.
/// </summary>
public sealed class RunwareOutpaintOptions
{
    [JsonPropertyName("top")]
    public int? Top { get; set; }

    [JsonPropertyName("bottom")]
    public int? Bottom { get; set; }

    [JsonPropertyName("left")]
    public int? Left { get; set; }

    [JsonPropertyName("right")]
    public int? Right { get; set; }
}

