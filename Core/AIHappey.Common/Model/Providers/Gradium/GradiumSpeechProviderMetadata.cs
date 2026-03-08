using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Gradium;

/// <summary>
/// Provider options for Gradium text-to-speech.
/// Consumed via <c>providerOptions.gradium</c>.
/// </summary>
public sealed class GradiumSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }

    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("json_config")]
    public string? JsonConfig { get; set; }

    [JsonPropertyName("only_audio")]
    public bool? OnlyAudio { get; set; }
}
