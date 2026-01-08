using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqCodeInterpreterContainer
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "auto";
}

