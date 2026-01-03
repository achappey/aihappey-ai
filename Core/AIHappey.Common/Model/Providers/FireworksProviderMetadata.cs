
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class FireworksImageProviderMetadata
{
   

}

public class FireworksProviderMetadata
{

    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}
