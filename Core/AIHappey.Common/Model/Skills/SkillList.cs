using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Skills;

public class DataList<T>
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    [JsonPropertyName("last_id")]
    public string LastId { get; set; } = default!;

    [JsonPropertyName("first_id")]
    public string FirstId { get; set; } = default!;

    [JsonPropertyName("data")]
    public IEnumerable<T>? Data { get; set; }
}