using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Cortecs;

public class CortecsProviderMetadata
{
    [JsonPropertyName("preference")]
    public string? Preference { get; set; }

    [JsonPropertyName("allowed_providers")]
    public IEnumerable<string>? AllowedProviders { get; set; }

    [JsonPropertyName("eu_native")]
    public bool? EuNative { get; set; }

    [JsonPropertyName("allow_quantization")]
    public bool? AllowQuantization { get; set; }

    [JsonPropertyName("safe_prompt")]
    public bool? SafePrompt { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }

    [JsonPropertyName("stop")]
    public IEnumerable<string>? Stop { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("n")]
    public int? N { get; set; }

    [JsonPropertyName("logprobs")]
    public object? LogProbs { get; set; }

    [JsonPropertyName("prediction")]
    public object? Prediction { get; set; }
}

