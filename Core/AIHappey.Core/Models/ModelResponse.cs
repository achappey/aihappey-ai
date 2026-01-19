using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public class ModelResponse
{
    [JsonPropertyName("object")]
    public string Object { get; } = "list";

    [JsonPropertyName("data")]
    public IEnumerable<Model> Data { get; set; } = [];
}
