using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class PerplexityProviderMetadata
{
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; } // e.g. "medium"  

    [JsonPropertyName("tools")]
    public JsonElement[]? Tools { get; set; }

    [JsonPropertyName("language_preference")]
    public string? LanguagePreference { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("max_steps")]
    public int? MaxSteps { get; set; }

    [JsonPropertyName("models")]
    public IEnumerable<string>? Models { get; set; }

}

