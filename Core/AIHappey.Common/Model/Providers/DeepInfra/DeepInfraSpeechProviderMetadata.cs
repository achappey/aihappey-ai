using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;


/// <summary>
/// Provider-specific options for DeepInfra Text-to-Speech inference.
/// Mirrors DeepInfra inference schema for TTS models.
/// </summary>
public sealed class DeepInfraSpeechProviderMetadata
{

    /// <summary>
    /// Service tier used for processing the request.
    /// Allowed values: "default" | "priority".
    /// </summary>
    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("resembleai")]
    public DeepInfraResembleAISpeechProviderMetadata? ResembleAI { get; set; }

    [JsonPropertyName("hexgrad")]
    public DeepInfraHexgradSpeechProviderMetadata? Hexgrad { get; set; }

    [JsonPropertyName("sesame")]
    public DeepInfraSesameSpeechProviderMetadata? Sesame { get; set; }

    [JsonPropertyName("canopylabs")]
    public DeepInfraCanopyLabsSpeechProviderMetadata? CanopyLabs { get; set; }

    [JsonPropertyName("zyphra")]
    public DeepInfraZyphraSpeechProviderMetadata? Zyphra { get; set; }

}
