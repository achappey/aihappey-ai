using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqReasoning
{
    [JsonPropertyName("effort")]
    public string? Effort { get; set; } // low, medium, high
}

