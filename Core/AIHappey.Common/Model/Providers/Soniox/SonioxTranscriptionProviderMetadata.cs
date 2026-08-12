using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Soniox;

/// <summary>
/// Provider options for Soniox asynchronous speech-to-text requests.
/// Consumed via <c>providerOptions.soniox</c>.
/// </summary>
public sealed class SonioxTranscriptionProviderMetadata
{
    [JsonPropertyName("language_hints")]
    public IReadOnlyList<string>? LanguageHints { get; set; }

    [JsonPropertyName("language_hints_strict")]
    public bool? LanguageHintsStrict { get; set; }

    [JsonPropertyName("enable_speaker_diarization")]
    public bool? EnableSpeakerDiarization { get; set; }

    [JsonPropertyName("enable_language_identification")]
    public bool? EnableLanguageIdentification { get; set; }

    [JsonPropertyName("context")]
    public object? Context { get; set; }

    [JsonPropertyName("client_reference_id")]
    public string? ClientReferenceId { get; set; }
}
