using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Skills;

public class SkillVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "skill.version";

    [JsonPropertyName("created_at")]
    public long? CreatedAt { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = default!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = default!;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("skill_id")]
    public string? SkillId { get; set; }
}