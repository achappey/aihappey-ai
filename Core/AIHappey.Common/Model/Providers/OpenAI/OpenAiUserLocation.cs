using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiUserLocation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "approximate";

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }
}

