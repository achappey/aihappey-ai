using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class IconGeneration
{
    /// <summary>
    /// Icon style.
    /// </summary>
    /// <remarks>Allowed values: solid, outline, color, flat, sticker.</remarks>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// Number of inference steps.
    /// </summary>
    /// <remarks>Range: 10-50.</remarks>
    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    /// <summary>
    /// Guidance scale.
    /// </summary>
    /// <remarks>Range: 0-10.</remarks>
    [JsonPropertyName("guidance_scale")]
    public double? GuidanceScale { get; set; }

    /// <summary>
    /// Output format for icon generation.
    /// </summary>
    /// <remarks>Allowed values: png, svg. Only used for the non-preview endpoint.</remarks>
    [JsonPropertyName("format")]
    public string? Format { get; set; }
}

