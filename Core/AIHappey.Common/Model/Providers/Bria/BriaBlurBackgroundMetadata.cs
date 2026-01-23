using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaBlurBackgroundMetadata
{

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

}
