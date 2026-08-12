using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.EverypixelLabs;

public sealed class EverypixelLabsSpeechProviderMetadata
{
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }

}

