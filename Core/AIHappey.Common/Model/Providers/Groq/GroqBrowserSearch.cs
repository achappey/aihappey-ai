using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Groq;

public sealed class GroqBrowserSearch
{
    [JsonPropertyName("type")]
    public string? Type { get; set; } = "browser_search";
}

