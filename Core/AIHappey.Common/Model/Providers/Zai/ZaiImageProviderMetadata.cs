namespace AIHappey.Common.Model.Providers.Zai;

/// <summary>
/// Provider-specific metadata for Z.AI image generation.
/// </summary>
public sealed class ZaiImageProviderMetadata
{
    /// <summary>
    /// The quality of the generated image.
    /// <list type="bullet">
    /// <item><description><c>hd</c>: higher detail and consistency (slower; ~20s)</description></item>
    /// <item><description><c>standard</c>: faster generation (~5-10s)</description></item>
    /// </list>
    /// <para>
    /// Notes from Z.AI docs: <c>glm-image</c> default is <c>hd</c>, other models default is <c>standard</c>.
    /// </para>
    /// </summary>
    public string? Quality { get; set; } = "hd";
}

