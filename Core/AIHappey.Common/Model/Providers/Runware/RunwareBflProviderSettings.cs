using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Black Forest Labs (BFL) provider settings for Runware-hosted FLUX models.
/// This maps to Runware's <c>providerSettings.bfl</c> object.
/// </summary>
public sealed class RunwareBflProviderSettings
{
    /// <summary>
    /// Enables BFL prompt upsampling.
    /// </summary>
    [JsonPropertyName("promptUpsampling")]
    public bool? PromptUpsampling { get; set; }

    /// <summary>
    /// Adjusts safety tolerance (provider-specific scale).
    /// </summary>
    [JsonPropertyName("safetyTolerance")]
    public int? SafetyTolerance { get; set; }

    /// <summary>
    /// Enables raw output mode (supported by specific BFL models, e.g. FLUX.1.1 Pro Ultra).
    /// </summary>
    [JsonPropertyName("raw")]
    public bool? Raw { get; set; }

    public bool HasAnyValue()
        => PromptUpsampling is not null
            || SafetyTolerance is not null
            || Raw is not null;
}

