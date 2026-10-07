using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>AI SDK DecisionModelV4 wire request for POST /api/decisions, with a gateway model ID.</summary>
public sealed class DecisionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    /// <summary>One shared state, including when its value is an array.</summary>
    [JsonPropertyName("state")]
    public DecisionInput State { get; set; } = null!;

    [JsonPropertyName("questions")]
    public Dictionary<string, DecisionQuestion> Questions { get; set; } = [];

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string?>? Headers { get; set; }

    [JsonPropertyName("providerOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    // AbortSignal is a client-side object; cancellation uses the HTTP request token.
}

/// <summary>A string, JSON object, or JSON array; arbitrary structured JSON is preserved.</summary>
[JsonConverter(typeof(DecisionInputJsonConverter))]
public sealed class DecisionInput
{
    public JsonElement Value { get; }

    public DecisionInput(string text)
        : this(JsonSerializer.SerializeToElement(text ?? throw new ArgumentNullException(nameof(text)))) { }

    public DecisionInput(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new ArgumentException("Decision input must be a string, JSON object, or JSON array.", nameof(value));

        Value = value.Clone();
    }

    public static implicit operator DecisionInput(string text) => new(text);
    public static implicit operator DecisionInput(JsonElement value) => new(value);
}

[JsonConverter(typeof(DecisionQuestionJsonConverter))]
public abstract class DecisionQuestion(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;

    [JsonPropertyName("instructions")]
    public DecisionInput Instructions { get; set; } = null!;
}

public sealed class DecisionChoiceQuestion() : DecisionQuestion("choice")
{
    /// <summary>Nonempty option-name map. A null value means no description.</summary>
    [JsonPropertyName("criteria")]
    public Dictionary<string, DecisionInput?> Criteria { get; set; } = [];
}

public sealed class DecisionScoreQuestion() : DecisionQuestion("score")
{
    /// <summary>At least two ordered levels, indexed from zero. Null levels have no description.</summary>
    [JsonPropertyName("criteria")]
    public IReadOnlyList<DecisionInput?> Criteria { get; set; } = [];
}

public sealed class DecisionBooleanQuestion() : DecisionQuestion("boolean")
{
    [JsonPropertyName("criteria")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionBooleanCriteria? Criteria { get; set; }
}

[JsonConverter(typeof(DecisionBooleanCriteriaJsonConverter))]
public sealed class DecisionBooleanCriteria
{
    private DecisionInput? trueCriteria;
    private DecisionInput? falseCriteria;

    [JsonPropertyName("true")]
    public DecisionInput? True
    {
        get => trueCriteria;
        set { trueCriteria = value; HasTrue = true; }
    }

    [JsonPropertyName("false")]
    public DecisionInput? False
    {
        get => falseCriteria;
        set { falseCriteria = value; HasFalse = true; }
    }

    internal bool HasTrue { get; private set; }
    internal bool HasFalse { get; private set; }
}

public sealed class DecisionResponse
{
    /// <summary>Exactly one answer per question, under the original question IDs.</summary>
    [JsonPropertyName("answers")]
    public Dictionary<string, DecisionAnswer> Answers { get; set; } = [];

    [JsonPropertyName("rounding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionRounding? Rounding { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionUsage? Usage { get; set; }

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DecisionResponseMetadata? Response { get; set; }
}

[JsonConverter(typeof(DecisionAnswerJsonConverter))]
public abstract class DecisionAnswer(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class DecisionChoiceAnswer() : DecisionAnswer("choice")
{
    /// <summary>Selected option, with maximal probability when a distribution exists.</summary>
    [JsonPropertyName("choice")]
    public string Choice { get; set; } = null!;

    [JsonPropertyName("probabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? Probabilities { get; set; }
}

public sealed class DecisionScoreAnswer() : DecisionAnswer("score")
{
    /// <summary>Fractional position in [0, levels - 1]; weighted mean if probabilities are supplied.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Complete distribution, keyed by zero-based level indices as strings.</summary>
    [JsonPropertyName("probabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? Probabilities { get; set; }
}

public sealed class DecisionBooleanAnswer() : DecisionAnswer("boolean")
{
    /// <summary>P(true) in [0, 1], not confidence in either outcome.</summary>
    [JsonPropertyName("probability")]
    public double Probability { get; set; }
}

public sealed class DecisionRefusalAnswer() : DecisionAnswer("refusal");

/// <summary>
/// Omit for full precision. Future distribution/weighted-score validation should allow half
/// a unit in the last decimal place per rounded value without changing those values.
/// </summary>
public sealed class DecisionRounding
{
    [JsonPropertyName("probabilityDecimals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProbabilityDecimals { get; set; }

    [JsonPropertyName("scoreDecimals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScoreDecimals { get; set; }
}

public sealed class DecisionUsage
{
    [JsonPropertyName("inputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputTokens { get; set; }
}

public sealed class DecisionResponseMetadata
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? Timestamp { get; set; }

    [JsonPropertyName("modelId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModelId { get; set; }

    [JsonPropertyName("headers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, string?>? Headers { get; set; }

    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Body { get; set; }
}
