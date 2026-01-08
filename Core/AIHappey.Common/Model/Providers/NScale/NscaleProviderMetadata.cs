using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.NScale;

public class NscaleProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }
}

