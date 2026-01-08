using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralWebSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search";
}

