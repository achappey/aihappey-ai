using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Soniox;

/// <summary>
/// Provider options for Soniox text-to-speech requests.
/// Consumed via <c>providerOptions.soniox</c>.
/// </summary>
public sealed class SonioxSpeechProviderMetadata
{
    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("bitrate")]
    public int? Bitrate { get; set; }

    [JsonPropertyName("client_reference_id")]
    public string? ClientReferenceId { get; set; }

    [JsonPropertyName("reduce_silence")]
    public bool? ReduceSilence { get; set; }
}
