using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Skills;

public class Skill
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "skill";

    [JsonPropertyName("created_at")]
    public long? CreatedAt { get; set; }

    [JsonPropertyName("default_version")]
    public string DefaultVersion { get; set; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("latest_version")]
    public string? LatestVersion { get; set; }
}