using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.ChatCompletions;

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
}