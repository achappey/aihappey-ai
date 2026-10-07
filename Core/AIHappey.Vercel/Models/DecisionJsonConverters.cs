using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public sealed class DecisionInputJsonConverter : JsonConverter<DecisionInput>
{
    public override DecisionInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array))
            throw new JsonException("Decision input must be a string, JSON object, or JSON array.");

        return new(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, DecisionInput value, JsonSerializerOptions options)
        => value.Value.WriteTo(writer);
}

/// <summary>Preserves the distinction between omitted and explicitly null boolean criteria.</summary>
public sealed class DecisionBooleanCriteriaJsonConverter : JsonConverter<DecisionBooleanCriteria>
{
    public override DecisionBooleanCriteria Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Decision boolean criteria must be an object.");

        var result = new DecisionBooleanCriteria();
        if (root.TryGetProperty("true", out var trueValue))
            result.True = trueValue.Deserialize<DecisionInput>(options);
        if (root.TryGetProperty("false", out var falseValue))
            result.False = falseValue.Deserialize<DecisionInput>(options);
        return result;
    }

    public override void Write(Utf8JsonWriter writer, DecisionBooleanCriteria value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.HasTrue)
        {
            writer.WritePropertyName("true");
            JsonSerializer.Serialize(writer, value.True, options);
        }
        if (value.HasFalse)
        {
            writer.WritePropertyName("false");
            JsonSerializer.Serialize(writer, value.False, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary>Reads the discriminator anywhere in the object without changing host-wide JSON settings.</summary>
public abstract class DecisionVariantJsonConverter<T> : JsonConverter<T>
{
    protected abstract Type GetVariantType(string? type);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var discriminator)
            || discriminator.ValueKind != JsonValueKind.String)
            throw new JsonException("A decision variant requires a string 'type' discriminator.");

        return (T)(root.Deserialize(GetVariantType(discriminator.GetString()), options)
            ?? throw new JsonException("Decision variant cannot be null."));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value!.GetType(), options);
}

public sealed class DecisionQuestionJsonConverter : DecisionVariantJsonConverter<DecisionQuestion>
{
    protected override Type GetVariantType(string? type) => type switch
    {
        "choice" => typeof(DecisionChoiceQuestion),
        "score" => typeof(DecisionScoreQuestion),
        "boolean" => typeof(DecisionBooleanQuestion),
        _ => throw new JsonException($"Unsupported decision question type '{type}'.")
    };
}

public sealed class DecisionAnswerJsonConverter : DecisionVariantJsonConverter<DecisionAnswer>
{
    protected override Type GetVariantType(string? type) => type switch
    {
        "choice" => typeof(DecisionChoiceAnswer),
        "score" => typeof(DecisionScoreAnswer),
        "boolean" => typeof(DecisionBooleanAnswer),
        "refusal" => typeof(DecisionRefusalAnswer),
        _ => throw new JsonException($"Unsupported decision answer type '{type}'.")
    };
}
