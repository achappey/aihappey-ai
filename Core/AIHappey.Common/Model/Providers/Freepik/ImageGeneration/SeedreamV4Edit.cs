namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

/// <summary>
/// ProviderOptions payload for Freepik "Seedream v4 Edit".
/// </summary>
/// <remarks>
/// Maps 1:1 to <c>POST /v1/ai/text-to-image/seedream-v4-edit</c> body, excluding <c>prompt</c>, <c>seed</c>,
/// <c>aspect_ratio</c>, and <c>webhook_url</c>.
/// </remarks>
public sealed class SeedreamV4Edit
{
    // No extra options documented beyond the base Seedream request + reference_images.
}

