using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public interface IOpenAITool
{
    string Type { get; set; }
}

public sealed class ShellTool : IOpenAITool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "shell";

    [JsonPropertyName("environment")]
    [JsonConverter(typeof(ShellEnvironmentUnionConverter))]
    public ShellEnvironmentUnion? Environment { get; set; }
}

public sealed class LocalShellTool : IOpenAITool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "local_shell";
}


#region Request environments

[JsonConverter(typeof(ShellEnvironmentUnionConverter))]
public abstract class ShellEnvironmentUnion
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ContainerAutoEnvironment : ShellEnvironmentUnion
{
    public override string Type => "container_auto";

    [JsonPropertyName("file_ids")]
    public List<string>? FileIds { get; set; }

    [JsonPropertyName("memory_limit")]
    public string? MemoryLimit { get; set; } // "1g" | "4g" | "16g" | "64g"

    [JsonPropertyName("network_policy")]
    [JsonConverter(typeof(ContainerNetworkPolicyUnionConverter))]
    public ContainerNetworkPolicyUnion? NetworkPolicy { get; set; }

    [JsonPropertyName("skills")]
    public List<SkillUnion>? Skills { get; set; }
}

public sealed class ContainerReferenceEnvironment : ShellEnvironmentUnion
{
    public override string Type => "container_reference";

    [JsonPropertyName("container_id")]
    public string ContainerId { get; set; } = null!;
}

public sealed class LocalEnvironment : ShellEnvironmentUnion
{
    public override string Type => "local";

    [JsonPropertyName("skills")]
    public List<SkillUnion>? Skills { get; set; }
}

#endregion

#region Network policy

[JsonConverter(typeof(ContainerNetworkPolicyUnionConverter))]
public abstract class ContainerNetworkPolicyUnion
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ContainerNetworkPolicyDisabled : ContainerNetworkPolicyUnion
{
    public override string Type => "disabled";
}

public sealed class ContainerNetworkPolicyAllowlist : ContainerNetworkPolicyUnion
{
    public override string Type => "allowlist";

    [JsonPropertyName("allowed_domains")]
    public List<string> AllowedDomains { get; set; } = [];

    [JsonPropertyName("domain_secrets")]
    public List<ContainerNetworkPolicyDomainSecret>? DomainSecrets { get; set; }
}

public sealed class ContainerNetworkPolicyDomainSecret
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = null!;

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("value")]
    public string Value { get; set; } = null!;
}

#endregion

#region Skills

[JsonConverter(typeof(SkillUnionConverter))]
public abstract class SkillUnion
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class SkillReference : SkillUnion
{
    public override string Type => "skill_reference";

    [JsonPropertyName("skill_id")]
    public string SkillId { get; set; } = null!;

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class InlineSkill : SkillUnion
{
    public override string Type => "inline";

    [JsonPropertyName("name")]
    public string Name { get; set; } = null!;

    [JsonPropertyName("description")]
    public string Description { get; set; } = null!;

    [JsonPropertyName("source")]
    public InlineSkillSource Source { get; set; } = null!;
}

public sealed class InlineSkillSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "content";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "application/zip";

    [JsonPropertyName("data")]
    public string Data { get; set; } = null!; // base64 zip
}

#endregion

#region Hosted shell response items

public sealed class ShellCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "shell_call";

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = null!;

    [JsonPropertyName("status")]
    public string? Status { get; set; } // in_progress | completed | incomplete

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("action")]
    public ShellCallAction Action { get; set; } = null!;

    [JsonPropertyName("environment")]
    [JsonConverter(typeof(ShellCallEnvironmentUnionConverter))]
    public ShellCallEnvironmentUnion? Environment { get; set; }
}

public sealed class ShellCallAction
{
    [JsonPropertyName("commands")]
    public List<string> Commands { get; set; } = [];

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; set; }

    [JsonPropertyName("max_output_length")]
    public int? MaxOutputLength { get; set; }
}

[JsonConverter(typeof(ShellCallEnvironmentUnionConverter))]
public abstract class ShellCallEnvironmentUnion
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ResponseLocalEnvironment : ShellCallEnvironmentUnion
{
    public override string Type => "local";
}

public sealed class ResponseContainerReference : ShellCallEnvironmentUnion
{
    public override string Type => "container_reference";

    [JsonPropertyName("container_id")]
    public string ContainerId { get; set; } = null!;
}

public sealed class ShellCallOutput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "shell_call_output";

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = null!;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("max_output_length")]
    public int? MaxOutputLength { get; set; }

    [JsonPropertyName("output")]
    public List<ShellCallOutputChunk> Output { get; set; } = [];
}

public sealed class ShellCallOutputChunk
{
    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = "";

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = "";

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("outcome")]
    [JsonConverter(typeof(ShellOutcomeUnionConverter))]
    public ShellOutcomeUnion Outcome { get; set; } = null!;
}

[JsonConverter(typeof(ShellOutcomeUnionConverter))]
public abstract class ShellOutcomeUnion
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ShellExitOutcome : ShellOutcomeUnion
{
    public override string Type => "exit";

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }
}

public sealed class ShellTimeoutOutcome : ShellOutcomeUnion
{
    public override string Type => "timeout";
}

#endregion

#region Local shell response items

public sealed class LocalShellCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "local_shell_call";

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = null!;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("action")]
    public LocalShellAction Action { get; set; } = null!;
}

public sealed class LocalShellAction
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "exec";

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }

    [JsonPropertyName("timeout_ms")]
    public int? TimeoutMs { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("working_directory")]
    public string? WorkingDirectory { get; set; }
}

public sealed class LocalShellCallOutput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "local_shell_call_output";

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // OpenAI reference says this is a JSON string, not an object.
    [JsonPropertyName("output")]
    public string Output { get; set; } = "";
}

#endregion

#region Converters

public sealed class ShellEnvironmentUnionConverter : JsonConverter<ShellEnvironmentUnion>
{
    public override ShellEnvironmentUnion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing environment.type");

        var type = typeProp.GetString();
        var json = root.GetRawText();

        return type switch
        {
            "container_auto" => JsonSerializer.Deserialize<ContainerAutoEnvironment>(json, options),
            "container_reference" => JsonSerializer.Deserialize<ContainerReferenceEnvironment>(json, options),
            "local" => JsonSerializer.Deserialize<LocalEnvironment>(json, options),
            _ => throw new JsonException($"Unknown shell environment type '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ShellEnvironmentUnion value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

public sealed class ContainerNetworkPolicyUnionConverter : JsonConverter<ContainerNetworkPolicyUnion>
{
    public override ContainerNetworkPolicyUnion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing network_policy.type");

        var type = typeProp.GetString();
        var json = root.GetRawText();

        return type switch
        {
            "disabled" => JsonSerializer.Deserialize<ContainerNetworkPolicyDisabled>(json, options),
            "allowlist" => JsonSerializer.Deserialize<ContainerNetworkPolicyAllowlist>(json, options),
            _ => throw new JsonException($"Unknown network policy type '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ContainerNetworkPolicyUnion value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

public sealed class SkillUnionConverter : JsonConverter<SkillUnion>
{
    public override SkillUnion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing skill.type");

        var type = typeProp.GetString();
        var json = root.GetRawText();

        return type switch
        {
            "skill_reference" => JsonSerializer.Deserialize<SkillReference>(json, options),
            "inline" => JsonSerializer.Deserialize<InlineSkill>(json, options),
            _ => throw new JsonException($"Unknown skill type '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, SkillUnion value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

public sealed class ShellCallEnvironmentUnionConverter : JsonConverter<ShellCallEnvironmentUnion>
{
    public override ShellCallEnvironmentUnion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing shell_call.environment.type");

        var type = typeProp.GetString();
        var json = root.GetRawText();

        return type switch
        {
            "local" => JsonSerializer.Deserialize<ResponseLocalEnvironment>(json, options),
            "container_reference" => JsonSerializer.Deserialize<ResponseContainerReference>(json, options),
            _ => throw new JsonException($"Unknown shell_call environment type '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ShellCallEnvironmentUnion value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

public sealed class ShellOutcomeUnionConverter : JsonConverter<ShellOutcomeUnion>
{
    public override ShellOutcomeUnion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing outcome.type");

        var type = typeProp.GetString();
        var json = root.GetRawText();

        return type switch
        {
            "exit" => JsonSerializer.Deserialize<ShellExitOutcome>(json, options),
            "timeout" => JsonSerializer.Deserialize<ShellTimeoutOutcome>(json, options),
            _ => throw new JsonException($"Unknown shell outcome type '{type}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ShellOutcomeUnion value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
}

#endregion