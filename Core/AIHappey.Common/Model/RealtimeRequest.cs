using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

public class RealtimeRequest
{

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
}


public class RealtimeResponse
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = null!;

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }
}
