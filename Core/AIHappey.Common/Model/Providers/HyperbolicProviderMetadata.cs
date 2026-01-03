
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class HyperbolicImageProviderMetadata
{

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

}

public class HyperbolicProviderMetadata
{

}
