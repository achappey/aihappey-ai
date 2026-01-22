using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "Seedream".
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image/seedream</c> body, excluding <c>prompt</c>, <c>seed</c>,
/// <c>aspect_ratio</c>, and <c>webhook_url</c>.
/// </remarks>
public sealed class Seedream
{
    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }
}

