using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware Google providerSettings.
/// </summary>
public sealed class RunwareGoogleProviderSettings
{
    /// <summary>
    /// Whether Runware/Google should enhance the prompt.
    /// </summary>
    [JsonPropertyName("enhancePrompt")]
    public bool? EnhancePrompt { get; set; }
}

