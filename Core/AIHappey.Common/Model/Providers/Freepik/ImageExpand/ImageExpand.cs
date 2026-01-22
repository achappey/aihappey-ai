using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class ImageExpand
{
    /// <summary>
    /// Pixels to expand on the left.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("left")]
    public int? Left { get; set; }

    /// <summary>
    /// Pixels to expand on the right.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("right")]
    public int? Right { get; set; }

    /// <summary>
    /// Pixels to expand on the top.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("top")]
    public int? Top { get; set; }

    /// <summary>
    /// Pixels to expand on the bottom.
    /// </summary>
    /// <remarks>Range: 0-2048.</remarks>
    [JsonPropertyName("bottom")]
    public int? Bottom { get; set; }
}

