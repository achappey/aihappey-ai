using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Sarvam;

public sealed class SarvamTranslationProviderMetadata
{
    [JsonPropertyName("speaker_gender")]
    public string? SpeakerGender { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("output_script")]
    public string? OutputScript { get; set; }

    [JsonPropertyName("numerals_format")]
    public string? NumeralsFormat { get; set; }
}
