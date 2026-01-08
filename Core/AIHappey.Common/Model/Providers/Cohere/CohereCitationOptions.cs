using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cohere;

public class CohereCitationOptions
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "enabled"; // ENABLE,  DISABLED, FAST, ACCURATE, OFF
}

