using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraImageProviderMetadata
{
    [JsonPropertyName("bria")]
    public DeepInfraBriaImageProviderMetadata? Bria { get; set; }

    [JsonPropertyName("bria_enhance")]
    public DeepInfraBriaEnhanceImageProviderMetadata? BriaEnhance { get; set; }
}

public sealed class DeepInfraBriaEnhanceImageProviderMetadata
{
    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("steps_num")]
    public int? StepsNum { get; set; }

     [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

}