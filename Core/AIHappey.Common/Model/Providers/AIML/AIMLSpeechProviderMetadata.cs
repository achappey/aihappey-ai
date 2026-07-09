using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.AIML;

/// <summary>
/// Raw provider-specific options for AIML text-to-speech models routed through
/// <c>POST /v1/tts</c>.
/// </summary>
public sealed class AIMLSpeechProviderMetadata
{
    /// <summary>
    /// Arbitrary AIML <c>/v1/tts</c> payload properties. The AIML speech provider forwards
    /// these values without model-specific validation and overlays non-null/non-empty unified
    /// speech fields afterward.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Options { get; set; } = [];
}
