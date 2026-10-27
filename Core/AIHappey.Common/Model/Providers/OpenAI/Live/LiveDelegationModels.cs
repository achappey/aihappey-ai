using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI.Live;

[JsonConverter(typeof(LiveDelegationJsonConverter))]
public abstract class LiveDelegation(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;

    [JsonPropertyName("acknowledgement_delay_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AcknowledgementDelayMs { get; set; }
}

public sealed class LiveClientDelegation() : LiveDelegation("client");

public sealed class LiveResponsesDelegation() : LiveDelegation("responses")
{
    [Required]
    [JsonPropertyName("responses")]
    public required LiveResponsesDelegationConfig Responses { get; set; }
}

public sealed class LiveDecisionsDelegation() : LiveDelegation("decisions")
{
    [Required]
    [JsonPropertyName("decisions")]
    public required LiveDecisionsDelegationConfig Decisions { get; set; }
}

public sealed class LiveResponsesDelegationConfig
{
    [Required]
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MaxOutputTokens { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; set; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveReasoningConfig? Reasoning { get; set; }

    [JsonPropertyName("service_tier")]
    [LiveAllowedValues("auto", "default", "fast_tier_temp_pilot", "flex", "priority", "ultrafast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveTextConfig? Text { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveToolChoice? ToolChoice { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LiveResponsesTool>? Tools { get; set; }
}

public sealed class LiveReasoningConfig
{
    [JsonPropertyName("effort")]
    [LiveAllowedValues("none", "minimal", "low", "medium", "high", "xhigh")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }

    [JsonPropertyName("summary")]
    [LiveAllowedValues("concise", "detailed", "auto")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }
}

public sealed class LiveTextConfig
{
    [JsonPropertyName("verbosity")]
    [LiveAllowedValues("low", "medium", "high")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; set; }
}

public sealed class LiveDecisionsDelegationConfig
{
    [Required]
    [JsonPropertyName("model")]
    public required string Model { get; set; }

    [Required]
    [JsonPropertyName("questions")]
    public required List<LiveDecisionQuestion> Questions { get; set; }
}

public sealed class LiveDecisionTrigger
{
    [JsonPropertyName("type")]
    [AllowedValues("decisions")]
    public string Type { get; set; } = "decisions";

    [Required]
    [AllowedValues("session.input_transcript.delta", "session.output_transcript.delta")]
    [JsonPropertyName("event_type")]
    public required string EventType { get; set; }

    [Required]
    [JsonPropertyName("decisions")]
    public required LiveTriggerDecisions Decisions { get; set; }
}

public sealed class LiveTriggerDecisions
{
    [Required]
    [JsonPropertyName("questions")]
    public required List<LiveDecisionQuestion> Questions { get; set; }
}

[JsonConverter(typeof(LiveDecisionQuestionJsonConverter))]
public abstract class LiveDecisionQuestion(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;

    [Required]
    [JsonPropertyName("instructions")]
    public required LiveDecisionInstructions Instructions { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public sealed class LiveChoiceQuestion() : LiveDecisionQuestion("choice")
{
    [Required]
    [JsonPropertyName("choices")]
    public required List<LiveDecisionChoice> Choices { get; set; }
}

public sealed class LivePredicateQuestion() : LiveDecisionQuestion("predicate");

public sealed class LiveScoreQuestion() : LiveDecisionQuestion("score")
{
    [Required]
    [JsonPropertyName("levels")]
    public required List<LiveDecisionLevel> Levels { get; set; }
}

public sealed class LiveDecisionChoice
{
    [Required]
    [JsonPropertyName("value")]
    public required LiveDecisionChoiceValue Value { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

[JsonConverter(typeof(LiveDecisionChoiceValueJsonConverter))]
public sealed class LiveDecisionChoiceValue
{
    public string? Text { get; }
    public bool? Boolean { get; }
    public LiveDecisionChoiceValue(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));
    public LiveDecisionChoiceValue(bool boolean) => Boolean = boolean;
    public static implicit operator LiveDecisionChoiceValue(string text) => new(text);
    public static implicit operator LiveDecisionChoiceValue(bool boolean) => new(boolean);
}

public sealed class LiveDecisionLevel
{
    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

[JsonConverter(typeof(LiveDecisionInstructionsJsonConverter))]
public sealed class LiveDecisionInstructions
{
    public string? Text { get; }
    public IReadOnlyList<LiveDecisionInput>? Parts { get; }
    public LiveDecisionInstructions(string text) => Text = text ?? throw new ArgumentNullException(nameof(text));
    public LiveDecisionInstructions(IReadOnlyList<LiveDecisionInput> parts) => Parts = parts ?? throw new ArgumentNullException(nameof(parts));
    public static implicit operator LiveDecisionInstructions(string text) => new(text);
}

[JsonConverter(typeof(LiveDecisionInputJsonConverter))]
public abstract class LiveDecisionInput(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveDecisionInputText() : LiveDecisionInput("input_text")
{
    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("text")]
    public required string Text { get; set; }
}

public sealed class LiveDecisionInputImage() : LiveDecisionInput("input_image")
{
    [Required]
    [JsonPropertyName("image_url")]
    public required string ImageUrl { get; set; }

    [JsonPropertyName("detail")]
    [LiveAllowedValues("low", "high", "auto", "original")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

public sealed class LiveDecisionInputJson() : LiveDecisionInput("input_json")
{
    [JsonPropertyName("data")]
    [JsonConverter(typeof(LiveDecisionJsonDataConverter))]
    public required JsonElement Data { get; set; }
}
