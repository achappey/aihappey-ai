using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class RelightAdvancedSettings
{
    [JsonPropertyName("whites")]
    public int? Whites { get; set; }

    [JsonPropertyName("blacks")]
    public int? Blacks { get; set; }

    [JsonPropertyName("brightness")]
    public int? Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public int? Contrast { get; set; }

    [JsonPropertyName("saturation")]
    public int? Saturation { get; set; }

    /// <summary>
    /// Relight engine.
    /// </summary>
    /// <remarks>Docs example uses "illusio".</remarks>
    [JsonPropertyName("engine")]
    public string? Engine { get; set; }

    /// <summary>
    /// Transfer light A.
    /// </summary>
    [JsonPropertyName("transfer_light_a")]
    public string? TransferLightA { get; set; }

    /// <summary>
    /// Transfer light B.
    /// </summary>
    [JsonPropertyName("transfer_light_b")]
    public string? TransferLightB { get; set; }

    [JsonPropertyName("fixed_generation")]
    public bool? FixedGeneration { get; set; }
}

