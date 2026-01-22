using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "HyperFlux".
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image/hyperflux</c> body, excluding <c>prompt</c>, <c>seed</c>,
/// <c>aspect_ratio</c>, and <c>webhook_url</c>.
/// </remarks>
public sealed class Hyperflux
{
    [JsonPropertyName("styling")]
    public FreepikStyling? Styling { get; set; }
}

