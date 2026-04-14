using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralProviderMetadata
{
    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }

    [JsonPropertyName("completion_args")]
    public Dictionary<string, object>? CompletionArgs { get; set; }

}

