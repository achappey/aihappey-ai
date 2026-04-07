using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralProviderMetadata
{
    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }
}

