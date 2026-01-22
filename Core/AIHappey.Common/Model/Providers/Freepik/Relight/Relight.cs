using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class Relight
{
    /// <summary>
    /// Base64 of the reference image used for light transfer.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="TransferLightFromLightmap"/>.
    /// Base64-only (no URLs/data URLs).
    /// </remarks>
    //   [JsonPropertyName("transfer_light_from_reference_image")]
    //   public string? TransferLightFromReferenceImage { get; set; }

    /// <summary>
    /// Base64 of the lightmap used for light transfer.
    /// </summary>
    /// <remarks>
    /// Mutually exclusive with <see cref="TransferLightFromReferenceImage"/>.
    /// Base64-only (no URLs/data URLs).
    /// </remarks>
    // [JsonPropertyName("transfer_light_from_lightmap")]
    // public string? TransferLightFromLightmap { get; set; }

    /// <summary>
    /// Light transfer strength (0-100).
    /// </summary>
    [JsonPropertyName("light_transfer_strength")]
    public int? LightTransferStrength { get; set; }

    /// <summary>
    /// Interpolate from original.
    /// </summary>
    [JsonPropertyName("interpolate_from_original")]
    public bool? InterpolateFromOriginal { get; set; }

    /// <summary>
    /// Change background.
    /// </summary>
    [JsonPropertyName("change_background")]
    public bool? ChangeBackground { get; set; }

    /// <summary>
    /// Relight style.
    /// </summary>
    /// <remarks>
    /// Allowed values (per docs): standard, darker_but_realistic, clean, smooth, brighter,
    /// contrasted_n_hdr, just_composition.
    /// </remarks>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Preserve details.
    /// </summary>
    [JsonPropertyName("preserve_details")]
    public bool? PreserveDetails { get; set; }

    /// <summary>
    /// Advanced settings.
    /// </summary>
    [JsonPropertyName("advanced_settings")]
    public RelightAdvancedSettings? AdvancedSettings { get; set; }
}

