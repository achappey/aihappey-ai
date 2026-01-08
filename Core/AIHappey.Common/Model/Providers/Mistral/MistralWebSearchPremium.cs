using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralWebSearchPremium
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search_premium";
}

