using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

public sealed class ResponseShellCallEnvironmentJsonConverter : JsonConverter<ResponseShellCallEnvironment>
{
    public override ResponseShellCallEnvironment Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeProperty)
            ? typeProperty.GetString()
            : null;

        return type switch
        {
            "local" => root.Deserialize<ResponseShellLocalEnvironment>(options)
                ?? throw new JsonException("Could not deserialize local shell environment."),
            "container_reference" => root.Deserialize<ResponseShellContainerReferenceEnvironment>(options)
                ?? throw new JsonException("Could not deserialize shell container reference."),
            _ => throw new JsonException($"Unknown shell_call environment type: '{type ?? "(missing)"}'.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResponseShellCallEnvironment value,
        JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

public sealed class ResponseShellOutcomeJsonConverter : JsonConverter<ResponseShellOutcome>
{
    public override ResponseShellOutcome Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeProperty)
            ? typeProperty.GetString()
            : null;

        return type switch
        {
            "exit" => root.Deserialize<ResponseShellExitOutcome>(options)
                ?? throw new JsonException("Could not deserialize shell exit outcome."),
            "timeout" => root.Deserialize<ResponseShellTimeoutOutcome>(options)
                ?? throw new JsonException("Could not deserialize shell timeout outcome."),
            _ => throw new JsonException($"Unknown shell outcome type: '{type ?? "(missing)"}'.")
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResponseShellOutcome value,
        JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}
