using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik;

public sealed class Upscaler
{
    /// <summary>
    /// Upscaler Creative settings (POST /v1/ai/image-upscaler).
    /// </summary>
    [JsonPropertyName("creative")]
    public UpscalerCreative? Creative { get; set; }

    /// <summary>
    /// Upscaler Precision V1 settings (POST /v1/ai/image-upscaler-precision).
    /// </summary>
    [JsonPropertyName("precision")]
    public UpscalerPrecisionV1? Precision { get; set; }

    /// <summary>
    /// Upscaler Precision V2 settings (POST /v1/ai/image-upscaler-precision-v2).
    /// </summary>
    [JsonPropertyName("precision_v2")]
    public UpscalerPrecisionV2? PrecisionV2 { get; set; }
}

