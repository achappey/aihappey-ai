using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class ImageGeneration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_generation";

    [JsonPropertyName("background")]
    public string? Background { get; set; } = "auto";
    // transparent | opaque | auto

    [JsonPropertyName("input_fidelity")]
    public string? InputFidelity { get; set; } = "low";
    // high | low

    [JsonPropertyName("input_image_mask")]
    public InputImageMask? InputImageMask { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; } = "gpt-image-1.5";

    [JsonPropertyName("moderation")]
    public string? Moderation { get; set; } = "auto";

    [JsonPropertyName("output_compression")]
    public int? OutputCompression { get; set; } = 100;
    // 0–100

    [JsonPropertyName("partial_images")]
    public int? PartialImages { get; set; } = 0;
    // 0–3

    [JsonPropertyName("quality")]
    public string? Quality { get; set; } = "auto";
    // low | medium | high | auto

    [JsonPropertyName("size")]
    public string? Size { get; set; } = "auto";
    // 1024x1024 | 1024x1536 | 1536x1024 | auto
}

