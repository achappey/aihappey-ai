using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;


public class PollinationsImageProviderMetadata
{
    [JsonPropertyName("enhance")]
    public bool? Enhance { get; set; }

    [JsonPropertyName("private")]
    public bool? Private { get; set; }
}


public class PollinationsProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }



}
