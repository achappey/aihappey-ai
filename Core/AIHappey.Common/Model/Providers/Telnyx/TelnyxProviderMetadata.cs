using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Telnyx;

public sealed class TelnyxProviderMetadata
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

