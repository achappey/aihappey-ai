using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class SkinEnhancerFaithful : SkinEnhancerBase
{
    /// <summary>
    /// Skin detail enhancement level.
    /// </summary>
    /// <remarks>Range: 0-100.</remarks>
    [JsonPropertyName("skin_detail")]
    public int? SkinDetail { get; set; }
}

