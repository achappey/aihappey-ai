using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class SkinEnhancer
{
    /// <summary>
    /// Creative mode settings.
    /// </summary>
    [JsonPropertyName("creative")]
    public SkinEnhancerCreative? Creative { get; set; }

    /// <summary>
    /// Faithful mode settings.
    /// </summary>
    [JsonPropertyName("faithful")]
    public SkinEnhancerFaithful? Faithful { get; set; }

    /// <summary>
    /// Flexible mode settings.
    /// </summary>
    [JsonPropertyName("flexible")]
    public SkinEnhancerFlexible? Flexible { get; set; }
}

