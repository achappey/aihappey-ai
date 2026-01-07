using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class TelnyxProviderMetadata
{
    // Telnyx supports a vLLM-style guided decoding surface. We pass through
    // only what we model here.

    [JsonPropertyName("guided_json")]
    public object? GuidedJson { get; set; }

    [JsonPropertyName("guided_regex")]
    public string? GuidedRegex { get; set; }

    [JsonPropertyName("guided_choice")]
    public IEnumerable<string>? GuidedChoice { get; set; }

    [JsonPropertyName("min_p")]
    public float? MinP { get; set; }

    [JsonPropertyName("use_beam_search")]
    public bool? UseBeamSearch { get; set; }

    [JsonPropertyName("best_of")]
    public int? BestOf { get; set; }

    [JsonPropertyName("length_penalty")]
    public float? LengthPenalty { get; set; }

    [JsonPropertyName("early_stopping")]
    public bool? EarlyStopping { get; set; }

    [JsonPropertyName("logprobs")]
    public bool? Logprobs { get; set; }

    [JsonPropertyName("top_logprobs")]
    public int? TopLogprobs { get; set; }

    [JsonPropertyName("frequency_penalty")]
    public float? FrequencyPenalty { get; set; }

    [JsonPropertyName("presence_penalty")]
    public float? PresencePenalty { get; set; }
}

public class TelnyxTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// json | verbose_json
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    /// <summary>
    /// Only valid when response_format=verbose_json. Telnyx uses OpenAI naming:
    /// timestamp_granularities[]
    /// </summary>
    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

