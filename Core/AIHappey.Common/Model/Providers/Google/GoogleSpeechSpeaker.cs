using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Google;

public sealed class GoogleSpeechSpeaker
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }
}

