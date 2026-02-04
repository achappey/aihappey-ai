using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Reve;

/// <summary>
/// ProviderOptions schema for Reve image generation/edit/remix.
/// Consumed via <c>providerOptions.reve</c>.
/// </summary>
public sealed class ReveImageProviderMetadata
{
    [JsonPropertyName("test_time_scaling")]
    public double? TestTimeScaling { get; set; }

    [JsonPropertyName("postprocessing")]
    public IReadOnlyList<RevePostprocessing>? Postprocessing { get; set; }
}

public sealed class RevePostprocessing
{
    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("upscale_factor")]
    public int? UpscaleFactor { get; set; }

    [JsonPropertyName("max_dim")]
    public int? MaxDim { get; set; }

    [JsonPropertyName("max_width")]
    public int? MaxWidth { get; set; }

    [JsonPropertyName("max_height")]
    public int? MaxHeight { get; set; }
}
