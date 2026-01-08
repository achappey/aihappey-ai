using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralImageGeneration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_generation";
}

