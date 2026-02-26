using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.EverypixelLabs;

public sealed class EverypixelLabsSpeechProviderMetadata
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

}

