using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class RunwayImageProviderMetadata
{
    [JsonPropertyName("contentModeration")]
    public RunwayContentModeration? ContentModeration { get; set; }
}

public class RunwayContentModeration
{
    [JsonPropertyName("publicFigureThreshold")]
    public string? PublicFigureThreshold { get; set; }
}

