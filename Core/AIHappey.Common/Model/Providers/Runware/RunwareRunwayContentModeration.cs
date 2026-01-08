using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runway moderation options exposed through Runware's unified API.
/// </summary>
public sealed class RunwareRunwayContentModeration
{
    /// <summary>
    /// Runway-specific moderation threshold for public figure detection.
    /// </summary>
    [JsonPropertyName("publicFigureThreshold")]
    public float? PublicFigureThreshold { get; set; }
}

