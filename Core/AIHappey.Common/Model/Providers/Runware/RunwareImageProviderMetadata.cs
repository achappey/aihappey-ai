using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// ProviderOptions schema for Runware image inference.
/// Consumed via <c>providerOptions.runware</c> for <c>/v1/images/generations</c>.
/// </summary>
public class RunwareImageProviderMetadata
{
    [JsonPropertyName("outputType")]
    public string? OutputType { get; set; }

    [JsonPropertyName("outputFormat")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("outputQuality")]
    public int? OutputQuality { get; set; }

    [JsonPropertyName("deliveryMethod")]
    public string? DeliveryMethod { get; set; }

    [JsonPropertyName("includeCost")]
    public bool? IncludeCost { get; set; }

    [JsonPropertyName("ttl")]
    public int? Ttl { get; set; }

    [JsonPropertyName("negativePrompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("CFGScale")]
    public float? CFGScale { get; set; }

    [JsonPropertyName("clipSkip")]
    public int? ClipSkip { get; set; }

    [JsonPropertyName("seed")]
    public long? Seed { get; set; }

    [JsonPropertyName("strength")]
    public float? Strength { get; set; }

    [JsonPropertyName("safety")]
    public RunwareSafetyOptions? Safety { get; set; }

    /// <summary>
    /// Black Forest Labs (BFL) settings. This is translated to Runware's <c>providerSettings.bfl</c>.
    /// </summary>
    [JsonPropertyName("bfl")]
    public RunwareBflProviderSettings? Bfl { get; set; }

    /// <summary>
    /// Outpaint directions for BFL FLUX.1 Expand Pro (<c>bfl:1@3</c>).
    /// This is translated to Runware's top-level <c>outpaint</c> payload property.
    /// </summary>
    [JsonPropertyName("outpaint")]
    public RunwareOutpaintOptions? Outpaint { get; set; }

    /// <summary>
    /// Provider-specific options for models proxied via Runware.
    /// Mirrors Runware's <c>providerSettings</c> object.
    /// </summary>
    [JsonPropertyName("providerSettings")]
    public RunwareProviderSettings? ProviderSettings { get; set; }

    /// <summary>
    /// Raw pass-through for Runware Bria ControlNet input.
    /// This is forwarded to the top-level Runware payload key: <c>controlnet</c>.
    /// </summary>
    [JsonPropertyName("controlnet")]
    public JsonElement? ControlNet { get; set; }

    /// <summary>
    /// Raw pass-through for Runware Bria IP Adapter input.
    /// This is forwarded to the top-level Runware payload key: <c>ipAdapter</c>.
    /// </summary>
    [JsonPropertyName("ipAdapter")]
    public JsonElement? IpAdapter { get; set; }
}


