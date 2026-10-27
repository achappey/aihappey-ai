using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI.Live;

/// <summary>Discriminators may appear anywhere in the object, as in the published examples.</summary>
public abstract class LiveDiscriminatedJsonConverter<T> : JsonConverter<T> where T : class
{
    protected abstract Type? ResolveType(string discriminator);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var discriminator = ReadType(document.RootElement);
        var concreteType = ResolveType(discriminator)
            ?? throw new JsonException($"Unsupported {typeof(T).Name} type '{discriminator}'.");
        return (T)(document.RootElement.Deserialize(concreteType, options)
            ?? throw new JsonException($"Expected {typeof(T).Name}."));
    }

    internal static string ReadType(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()! : throw new JsonException("Expected an object with a string type discriminator.");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}

public sealed class LiveRequestTransportJsonConverter : LiveDiscriminatedJsonConverter<LiveRequestTransport>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "webrtc" => typeof(LiveWebRtcRequestTransport), "sip" => typeof(LiveSipRequestTransport), _ => null
    };
}

public sealed class LiveResponseTransportJsonConverter : LiveDiscriminatedJsonConverter<LiveResponseTransport>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "webrtc" => typeof(LiveWebRtcResponseTransport), "sip" => typeof(LiveSipResponseTransport), _ => null
    };
}

public sealed class LiveDelegationJsonConverter : LiveDiscriminatedJsonConverter<LiveDelegation>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "client" => typeof(LiveClientDelegation), "responses" => typeof(LiveResponsesDelegation),
        "decisions" => typeof(LiveDecisionsDelegation), _ => null
    };
}

public sealed class LiveDecisionQuestionJsonConverter : LiveDiscriminatedJsonConverter<LiveDecisionQuestion>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "choice" => typeof(LiveChoiceQuestion), "predicate" => typeof(LivePredicateQuestion),
        "score" => typeof(LiveScoreQuestion), _ => null
    };
}

public sealed class LiveDecisionInputJsonConverter : LiveDiscriminatedJsonConverter<LiveDecisionInput>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "input_text" => typeof(LiveDecisionInputText), "input_image" => typeof(LiveDecisionInputImage),
        "input_json" => typeof(LiveDecisionInputJson), _ => null
    };
}

public sealed class LiveToolSelectorJsonConverter : LiveDiscriminatedJsonConverter<LiveToolSelector>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "function" => typeof(LiveFunctionToolSelector), "mcp" => typeof(LiveMcpToolSelector),
        "custom" => typeof(LiveCustomToolSelector), "file_search" => typeof(LiveFileSearchToolSelector),
        "web_search" => typeof(LiveWebSearchToolSelector), "web_search_preview" => typeof(LiveWebSearchPreviewToolSelector),
        "image_generation" => typeof(LiveImageGenerationToolSelector), "computer" => typeof(LiveComputerToolSelector),
        "code_interpreter" => typeof(LiveCodeInterpreterToolSelector), "programmatic_tool_calling" => typeof(LiveProgrammaticToolSelector),
        "shell" => typeof(LiveShellToolSelector), "apply_patch" => typeof(LiveApplyPatchToolSelector), _ => null
    };
}

public sealed class LiveResponsesToolJsonConverter : LiveDiscriminatedJsonConverter<LiveResponsesTool>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "function" => typeof(LiveFunctionTool), "shell" => typeof(LiveShellTool), "web_search" => typeof(LiveWebSearchTool),
        "file_search" => typeof(LiveFileSearchTool), "code_interpreter" => typeof(LiveCodeInterpreterTool),
        "image_generation" => typeof(LiveImageGenerationTool), "mcp" => typeof(LiveMcpTool), "custom" => typeof(LiveCustomTool),
        "namespace" => typeof(LiveNamespaceTool), "tool_search" => typeof(LiveToolSearchTool),
        "programmatic_tool_calling" => typeof(LiveProgrammaticTool), "computer" => typeof(LiveComputerTool),
        "apply_patch" => typeof(LiveApplyPatchTool), _ => null
    };
}

public sealed class LiveShellEnvironmentJsonConverter : LiveDiscriminatedJsonConverter<LiveShellEnvironment>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "container_auto" => typeof(LiveContainerAutoEnvironment), "container_reference" => typeof(LiveContainerReferenceEnvironment),
        "local" => typeof(LiveLocalEnvironment), _ => null
    };
}

public sealed class LiveNetworkPolicyJsonConverter : LiveDiscriminatedJsonConverter<LiveNetworkPolicy>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "disabled" => typeof(LiveDisabledNetworkPolicy), "allowlist" => typeof(LiveAllowlistNetworkPolicy), _ => null
    };
}

public sealed class LiveSkillJsonConverter : LiveDiscriminatedJsonConverter<LiveSkill>
{
    protected override Type? ResolveType(string discriminator) => discriminator switch
    {
        "skill_reference" => typeof(LiveSkillReference), "inline" => typeof(LiveInlineSkill), _ => null
    };
}

public sealed class LiveVoiceJsonConverter : JsonConverter<LiveVoice>
{
    public override LiveVoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new LiveVoice(reader.GetString()!),
            JsonTokenType.StartObject => new LiveVoice(JsonSerializer.Deserialize<LiveCustomVoice>(ref reader, options)
                ?? throw new JsonException("Expected a custom voice object.")),
            _ => throw new JsonException("Expected a voice name or custom voice object.")
        };

    public override void Write(Utf8JsonWriter writer, LiveVoice value, JsonSerializerOptions options)
    {
        if (value.Name is not null) writer.WriteStringValue(value.Name);
        else JsonSerializer.Serialize(writer, value.Custom, options);
    }
}

public sealed class LiveEventPermissionsJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(LiveEventPermissions<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(PermissionsConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()), nonPublic: true)!;

    private sealed class PermissionsConverter<T> : JsonConverter<LiveEventPermissions<T>>
    {
        public override LiveEventPermissions<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType switch
            {
                JsonTokenType.String when reader.GetString() == "all" => LiveEventPermissions<T>.All(),
                JsonTokenType.StartArray => new LiveEventPermissions<T>(JsonSerializer.Deserialize<List<T>>(ref reader, options)!),
                _ => throw new JsonException("Event permissions must be 'all' or an array.")
            };

        public override void Write(Utf8JsonWriter writer, LiveEventPermissions<T> value, JsonSerializerOptions options)
        {
            if (value.IsAll) writer.WriteStringValue("all");
            else JsonSerializer.Serialize(writer, value.Events, options);
        }
    }
}

public sealed class LiveToolChoiceJsonConverter : JsonConverter<LiveToolChoice>
{
    public override LiveToolChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var mode = reader.GetString();
            return mode is "auto" or "none" or "required" ? new(mode) : throw new JsonException("Invalid tool choice mode.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var element = document.RootElement;
        return LiveDiscriminatedJsonConverter<LiveToolSelector>.ReadType(element) == "allowed_tools"
            ? new LiveToolChoice(element.Deserialize<LiveAllowedToolsChoice>(options)!)
            : new LiveToolChoice(element.Deserialize<LiveToolSelector>(options)!);
    }

    public override void Write(Utf8JsonWriter writer, LiveToolChoice value, JsonSerializerOptions options)
    {
        if (value.Mode is not null) writer.WriteStringValue(value.Mode);
        else if (value.Tool is not null) JsonSerializer.Serialize(writer, value.Tool, options);
        else JsonSerializer.Serialize(writer, value.AllowedTools, options);
    }
}

public sealed class LiveDecisionChoiceValueJsonConverter : JsonConverter<LiveDecisionChoiceValue>
{
    public override LiveDecisionChoiceValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.True or JsonTokenType.False => new(reader.GetBoolean()),
            _ => throw new JsonException("Decision choice value must be a string or boolean.")
        };

    public override void Write(Utf8JsonWriter writer, LiveDecisionChoiceValue value, JsonSerializerOptions options)
    {
        if (value.Boolean is { } boolean) writer.WriteBooleanValue(boolean);
        else writer.WriteStringValue(value.Text);
    }
}

public sealed class LiveDecisionInstructionsJsonConverter : JsonConverter<LiveDecisionInstructions>
{
    public override LiveDecisionInstructions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => new(reader.GetString()!),
            JsonTokenType.StartArray => new(JsonSerializer.Deserialize<List<LiveDecisionInput>>(ref reader, options)!),
            _ => throw new JsonException("Decision instructions must be text or an array of input parts.")
        };

    public override void Write(Utf8JsonWriter writer, LiveDecisionInstructions value, JsonSerializerOptions options)
    {
        if (value.Text is not null) writer.WriteStringValue(value.Text);
        else JsonSerializer.Serialize(writer, value.Parts, options);
    }
}

public sealed class LiveDecisionJsonDataConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        Validate(document.RootElement);
        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        Validate(value);
        value.WriteTo(writer);
    }

    private static void Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object && !(value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object)))
            throw new JsonException("Decision JSON input must be an object or an array of objects.");
    }
}
