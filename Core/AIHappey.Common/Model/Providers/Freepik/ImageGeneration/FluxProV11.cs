using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "Flux pro v1.1".
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image/flux-pro-v1-1</c> body, excluding <c>prompt</c>, <c>seed</c>,
/// <c>aspect_ratio</c>, and <c>webhook_url</c>.
/// </remarks>
public sealed class FluxProV11
{
    [JsonPropertyName("prompt_upsampling")]
    public bool? PromptUpsampling { get; set; }

    [JsonPropertyName("safety_tolerance")]
    public int? SafetyTolerance { get; set; }

    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; } // jpeg | png
}

