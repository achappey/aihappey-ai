using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiWebSearch
{
    [JsonPropertyName("user_location")]
    public OpenAiUserLocation? UserLocation { get; set; }
}

