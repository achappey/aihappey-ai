using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiWebSearch
{
    [JsonPropertyName("search_context_size")]
    public string? SearchContextSize { get; set; } // medium, high, low, etc.

    [JsonPropertyName("user_location")]
    public OpenAiUserLocation? UserLocation { get; set; }
}

