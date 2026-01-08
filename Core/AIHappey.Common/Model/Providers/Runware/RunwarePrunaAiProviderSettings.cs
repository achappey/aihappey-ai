using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware PrunaAI providerSettings.
/// </summary>
public sealed class RunwarePrunaAiProviderSettings
{
    /// <summary>
    /// Enables turbo mode for faster generation.
    /// </summary>
    [JsonPropertyName("turbo")]
    public bool? Turbo { get; set; }
}

