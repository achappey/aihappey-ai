using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Bria;

public class BriaImageProviderMetadata
{
    [JsonPropertyName("generateImage")]
    public BriaGenerateImageMetadata? GenerateImage { get; set; }

    [JsonPropertyName("imageEdit")]
    public BriaImageEditMetadata? ImageEdit { get; set; }

    [JsonPropertyName("reseasonImage")]
    public BriaReseasonImageProviderMetadata? ReseasonImage { get; set; }

    [JsonPropertyName("relightImage")]
    public BriaRelightImageProviderMetadata? RelightImage { get; set; }

    [JsonPropertyName("restyleImage")]
    public BriaRestyleImageProviderMetadata? RestyleImage { get; set; }

    [JsonPropertyName("colorize")]
    public BriaColorizeProviderMetadata? Colorize { get; set; }

    [JsonPropertyName("eraser")]
    public BriaEraserMetadata? Eraser { get; set; }

    [JsonPropertyName("replaceBackground")]
    public BriaReplaceBackgroundMetadata? ReplaceBackground { get; set; }

    [JsonPropertyName("removeBackground")]
    public BriaRemoveBackgroundMetadata? RemoveBackground { get; set; }

    [JsonPropertyName("generativeFill")]
    public BriaGenerativeFillMetadata? GenerativeFill { get; set; }

    [JsonPropertyName("blurBackground")]
    public BriaBlurBackgroundMetadata? BlurBackground { get; set; }

    [JsonPropertyName("eraseForeground")]
    public BriaEraseForegroundMetadata? EraseForeground { get; set; }

    [JsonPropertyName("enhanceImage")]
    public BriaEnhanceImageMetadata? EnhanceImage { get; set; }

    [JsonPropertyName("expandImage")]
    public BriaExpandImageMetadata? ExpandImage { get; set; }

    [JsonPropertyName("cropoutForeground")]
    public BriaCropoutForegroundMetadata? CropoutForeground { get; set; }

    [JsonPropertyName("increaseResolution")]
    public BriaIncreaseResolutionMetadata? IncreaseResolution { get; set; }

}

public class BriaReseasonImageProviderMetadata
{
    [JsonPropertyName("season")]
    public string? Season { get; set; } // "spring" "summer" "autumn" "winter"
}

public class BriaColorizeProviderMetadata
{
    [JsonPropertyName("style")]
    public string? Style { get; set; } //"color_contemporary"  "color_vivid" "decolorize" "sepia_vintage" "cinematic_lighting" "warm_golden"
}

public class BriaRestyleImageProviderMetadata
{
    [JsonPropertyName("style")]
    public string? Style { get; set; } // Enum: three_d_render, cubist_oil, renaissance_oil, anime, cartoon, coloring_book
}

public class BriaRelightImageProviderMetadata
{
    [JsonPropertyName("light_direction")]
    public string? LightDirection { get; set; } // "front" "side" "bottom" "top-down"

    [JsonPropertyName("light_type")]
    public string? LightType { get; set; } // Enum: midday, blue hour light, low-angle sunlight, sunrise light, spotlight on subject (keep background settings), overcast light, soft overcast daylight lighting, cloud-filtered lighting, fog-diffused lighting, moonlight lighting, starlight lighting nighttime, soft bokeh lighting, harsh studio lighting (keep background setting)


}
