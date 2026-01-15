using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Fireworks;

public class FireworksImageProviderMetadata
{
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; } // jpeg, png

    [JsonPropertyName("prompt_upsampling")]
    public bool? PromptUpsampling { get; set; }

    [JsonPropertyName("safety_tolerance")]
    public int? SafetyTolerance { get; set; }

    [JsonPropertyName("webhook_url")]
    public string? WebhookUrl { get; set; }
}