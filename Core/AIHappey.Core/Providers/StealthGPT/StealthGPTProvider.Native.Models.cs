using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.StealthGPT;

public partial class StealthGPTProvider
{
    private sealed class StealthGptProviderMetadata
    {
        [JsonPropertyName("rephrase")]
        public bool? Rephrase { get; set; }

        [JsonPropertyName("tone")]
        public string? Tone { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("qualityMode")]
        public string? QualityMode { get; set; }

        [JsonPropertyName("business")]
        public bool? Business { get; set; }

        [JsonPropertyName("isMultilingual")]
        public bool? IsMultilingual { get; set; }

        [JsonPropertyName("detector")]
        public string? Detector { get; set; }

        [JsonPropertyName("outputFormat")]
        public string? OutputFormat { get; set; }

        [JsonPropertyName("withImages")]
        public bool? WithImages { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }
    }

    private sealed class StealthGptStealthifyRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("rephrase")]
        public bool Rephrase { get; set; }

        [JsonPropertyName("tone")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tone { get; set; }

        [JsonPropertyName("mode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Mode { get; set; }

        [JsonPropertyName("qualityMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? QualityMode { get; set; }

        [JsonPropertyName("business")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Business { get; set; }

        [JsonPropertyName("isMultilingual")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? IsMultilingual { get; set; }

        [JsonPropertyName("detector")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Detector { get; set; }

        [JsonPropertyName("outputFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputFormat { get; set; }
    }

    private sealed class StealthGptArticlesRequest
    {
        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = string.Empty;

        [JsonPropertyName("withImages")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? WithImages { get; set; }

        [JsonPropertyName("size")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Size { get; set; }

        [JsonPropertyName("outputFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? OutputFormat { get; set; }
    }

    private sealed class StealthGptStealthifyResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("howLikelyToBeDetected")]
        public int? HowLikelyToBeDetected { get; set; }

        [JsonPropertyName("remainingCredits")]
        public int? RemainingCredits { get; set; }

        [JsonPropertyName("wordsSpent")]
        public int? WordsSpent { get; set; }

        [JsonPropertyName("billingMode")]
        public string? BillingMode { get; set; }

        [JsonPropertyName("meteredChargedCredits")]
        public int? MeteredChargedCredits { get; set; }

        [JsonPropertyName("tokensSpent")]
        public int? TokensSpent { get; set; }

        [JsonPropertyName("totalTokensSpent")]
        public int? TotalTokensSpent { get; set; }

        [JsonPropertyName("systemTokensSpent")]
        public int? SystemTokensSpent { get; set; }
    }

    private sealed class StealthGptArticlesResponse
    {
        [JsonPropertyName("result")]
        public string? Result { get; set; }

        [JsonPropertyName("remainingCredits")]
        public int? RemainingCredits { get; set; }
    }

    private sealed class StealthGptNativeResult
    {
        public string Model { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public string OutputText { get; init; } = string.Empty;
        public object RequestBody { get; init; } = default!;
        public object ResponseBody { get; init; } = default!;
        public object Usage { get; init; } = default!;
        public Dictionary<string, object?> ProviderMetadata { get; init; } = [];
    }
}
