using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runway;

public class RunwayContentModeration
{
    [JsonPropertyName("publicFigureThreshold")]
    public string? PublicFigureThreshold { get; set; }
}

