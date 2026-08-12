using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Soniox;

/// <summary>
/// Safe overrides for a single-use Soniox browser transcription key.
/// Consumed via <c>providerOptions.soniox</c>.
/// </summary>
public sealed class SonioxRealtimeProviderMetadata
{
    [JsonPropertyName("expires_in_seconds")]
    public int? ExpiresInSeconds { get; set; }

    [JsonPropertyName("max_session_duration_seconds")]
    public int? MaxSessionDurationSeconds { get; set; }

    [JsonPropertyName("client_reference_id")]
    public string? ClientReferenceId { get; set; }
}
