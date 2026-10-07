using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Models;

public sealed class OpenAIDecisionInputJsonConverter : JsonConverter<OpenAIDecisionInput>
{
    public override OpenAIDecisionInput Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.StartArray => new(JsonSerializer.Deserialize<OpenAIDecisionInputMessage[]>(ref reader, options)!),
            _ => throw new JsonException("Decision input must be a string or an array of user messages.")
        };

    public override void Write(Utf8JsonWriter writer, OpenAIDecisionInput value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
            writer.WriteStringValue(value.Text);
        else
            JsonSerializer.Serialize(writer, value.Messages, options);
    }
}

public sealed class OpenAIDecisionInputMessageJsonConverter : JsonConverter<OpenAIDecisionInputMessage>
{
    public override OpenAIDecisionInputMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("role", out var role)
            || role.ValueKind != JsonValueKind.String || role.GetString() != "user")
            throw new JsonException("Decision input messages require role 'user'.");

        string? messageType = null;
        if (root.TryGetProperty("type", out var type))
        {
            if (type.ValueKind != JsonValueKind.String || type.GetString() != "message")
                throw new JsonException("Decision input message type must be 'message' when supplied.");
            messageType = type.GetString();
        }

        if (!root.TryGetProperty("content", out var content))
            throw new JsonException("Decision input messages require content.");

        return new()
        {
            Type = messageType,
            Content = content.Deserialize<OpenAIDecisionMessageContent>(options)
                ?? throw new JsonException("Decision input message content cannot be null.")
        };
    }

    public override void Write(Utf8JsonWriter writer, OpenAIDecisionInputMessage value, JsonSerializerOptions options)
    {
        if (value.Type is not null and not "message")
            throw new JsonException("Decision input message type must be 'message' when supplied.");

        writer.WriteStartObject();
        writer.WriteString("role", "user");
        if (value.Type is not null)
            writer.WriteString("type", value.Type);
        writer.WritePropertyName("content");
        JsonSerializer.Serialize(writer, value.Content, options);
        writer.WriteEndObject();
    }
}

public sealed class OpenAIDecisionMessageContentJsonConverter : JsonConverter<OpenAIDecisionMessageContent>
{
    public override OpenAIDecisionMessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.StartArray => new(JsonSerializer.Deserialize<OpenAIDecisionInputPart[]>(ref reader, options)!),
            _ => throw new JsonException("Decision message content must be a string or an array of text/image parts.")
        };

    public override void Write(Utf8JsonWriter writer, OpenAIDecisionMessageContent value, JsonSerializerOptions options)
    {
        if (value.Text is not null)
            writer.WriteStringValue(value.Text);
        else
            JsonSerializer.Serialize(writer, value.Parts, options);
    }
}

public sealed class OpenAIDecisionChoiceValueJsonConverter : JsonConverter<OpenAIDecisionChoiceValue>
{
    public override OpenAIDecisionChoiceValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.True => new(true),
            JsonTokenType.False => new(false),
            _ => throw new JsonException("Decision choice values must be strings or booleans.")
        };

    public override void Write(Utf8JsonWriter writer, OpenAIDecisionChoiceValue value, JsonSerializerOptions options)
    {
        if (value.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.True or JsonValueKind.False))
            throw new JsonException("Decision choice values must be strings or booleans.");

        value.Value.WriteTo(writer);
    }
}

/// <summary>Reads the discriminator anywhere in the object without changing host-wide JSON settings.</summary>
public abstract class OpenAIDecisionVariantJsonConverter<T> : JsonConverter<T>
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

public sealed class OpenAIDecisionInputPartJsonConverter : OpenAIDecisionVariantJsonConverter<OpenAIDecisionInputPart>
{
    protected override Type GetVariantType(string? type) => type switch
    {
        "input_text" => typeof(OpenAIDecisionInputText),
        "input_image" => typeof(OpenAIDecisionInputImage),
        _ => throw new JsonException($"Unsupported decision input part type '{type}'.")
    };
}

public sealed class OpenAIDecisionQuestionJsonConverter : OpenAIDecisionVariantJsonConverter<OpenAIDecisionQuestion>
{
    protected override Type GetVariantType(string? type) => type switch
    {
        "predicate" => typeof(OpenAIDecisionPredicateQuestion),
        "choice" => typeof(OpenAIDecisionChoiceQuestion),
        "score" => typeof(OpenAIDecisionScoreQuestion),
        _ => throw new JsonException($"Unsupported decision question type '{type}'.")
    };
}

public sealed class OpenAIDecisionAnswerJsonConverter : OpenAIDecisionVariantJsonConverter<OpenAIDecisionAnswer>
{
    protected override Type GetVariantType(string? type) => type switch
    {
        "predicate" => typeof(OpenAIDecisionPredicateAnswer),
        "choice" => typeof(OpenAIDecisionChoiceAnswer),
        "score" => typeof(OpenAIDecisionScoreAnswer),
        "refusal" => typeof(OpenAIDecisionRefusalAnswer),
        _ => throw new JsonException($"Unsupported decision answer type '{type}'.")
    };
}
