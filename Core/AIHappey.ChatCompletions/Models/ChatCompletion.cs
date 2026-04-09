using System.Text.Json.Serialization;
using System.Text.Json;

namespace AIHappey.ChatCompletions.Models;

public class ChatCompletion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("choices")]
    public IEnumerable<object> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public object? Usage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}


public class ChatCompletionUpdate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("choices")]
    public IEnumerable<object> Choices { get; set; } = [];

    [JsonPropertyName("usage")]
    public object? Usage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
