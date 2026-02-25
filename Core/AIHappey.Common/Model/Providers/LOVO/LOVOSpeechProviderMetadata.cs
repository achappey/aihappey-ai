using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.LOVO;

/// <summary>
/// Provider options for LOVO TTS.
/// Consumed via <c>providerOptions.lovo</c>.
/// </summary>
public sealed class LOVOSpeechProviderMetadata
{
    /// <summary>
    /// Optional LOVO speaker style identifier.
    /// </summary>
    [JsonPropertyName("speakerStyle")]
    public string? SpeakerStyle { get; set; }
}

