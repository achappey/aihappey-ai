using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "Z-Image" endpoint (turbo model).
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image/z-image</c> body, excluding <c>prompt</c>, <c>seed</c>,
/// and <c>webhook_url</c>.
/// </remarks>
public sealed class ZImageTurboModel
{
    [JsonPropertyName("image_size")]
    public string? ImageSize { get; set; }

    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; } // png | jpeg

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }
}

