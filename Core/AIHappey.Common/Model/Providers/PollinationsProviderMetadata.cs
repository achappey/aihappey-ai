using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class PollinationsProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }



}
