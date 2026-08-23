using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.FreedomGPT;

/// <summary>
/// Provider options for FreedomGPT ActorGPT video generation.
/// Consumed through <c>providerOptions.freedomgpt</c>.
/// </summary>
public sealed class FreedomGPTVideoProviderMetadata
{
    /// <summary>
    /// ActorGPT actor identifier. Required when the selected model slug does not select an actor.
    /// </summary>
    [JsonPropertyName("actorId")]
    public string? ActorId { get; set; }

    /// <summary>
    /// ActorGPT voice identifier. Required when the selected model slug does not select a voice.
    /// </summary>
    [JsonPropertyName("voiceId")]
    public string? VoiceId { get; set; }
}
