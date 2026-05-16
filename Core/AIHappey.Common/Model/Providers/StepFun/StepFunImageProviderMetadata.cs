using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.StepFun;

public sealed class StepFunImageProviderMetadata
{
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("steps")]
    public int? Steps { get; set; }

    [JsonPropertyName("cfg_scale")]
    public double? CfgScale { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("text_mode")]
    public bool? TextMode { get; set; }
}
