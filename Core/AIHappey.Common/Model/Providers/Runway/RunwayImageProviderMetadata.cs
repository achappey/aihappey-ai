using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runway;

public class RunwayImageProviderMetadata
{
    [JsonPropertyName("contentModeration")]
    public RunwayContentModeration? ContentModeration { get; set; }
}

