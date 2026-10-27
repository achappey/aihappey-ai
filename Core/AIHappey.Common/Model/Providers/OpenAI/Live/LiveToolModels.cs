using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI.Live;

/// <summary>Scalar auto/none/required, a single tool selector, or an allowed_tools choice.</summary>
[JsonConverter(typeof(LiveToolChoiceJsonConverter))]
public sealed class LiveToolChoice
{
    public string? Mode { get; }
    public LiveToolSelector? Tool { get; }
    public LiveAllowedToolsChoice? AllowedTools { get; }

    public LiveToolChoice(string mode)
    {
        if (mode is not ("auto" or "none" or "required")) throw new ArgumentException("Invalid tool choice mode.", nameof(mode));
        Mode = mode;
    }

    public LiveToolChoice(LiveToolSelector tool) => Tool = tool ?? throw new ArgumentNullException(nameof(tool));
    public LiveToolChoice(LiveAllowedToolsChoice allowedTools) => AllowedTools = allowedTools ?? throw new ArgumentNullException(nameof(allowedTools));
    public static implicit operator LiveToolChoice(string mode) => new(mode);
}

public sealed class LiveAllowedToolsChoice
{
    [JsonPropertyName("type")]
    [AllowedValues("allowed_tools")]
    public string Type { get; set; } = "allowed_tools";

    [Required]
    [JsonPropertyName("tools")]
    public required List<LiveToolSelector> Tools { get; set; }

    [JsonPropertyName("mode")]
    [LiveAllowedValues("none", "auto", "required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }
}

[JsonConverter(typeof(LiveToolSelectorJsonConverter))]
public abstract class LiveToolSelector(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveFunctionToolSelector() : LiveToolSelector("function")
{
    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

public sealed class LiveMcpToolSelector() : LiveToolSelector("mcp")
{
    [Required]
    [JsonPropertyName("server_label")]
    public required string ServerLabel { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public sealed class LiveCustomToolSelector() : LiveToolSelector("custom")
{
    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }
}

public sealed class LiveFileSearchToolSelector() : LiveToolSelector("file_search");
public sealed class LiveWebSearchToolSelector() : LiveToolSelector("web_search");
public sealed class LiveWebSearchPreviewToolSelector() : LiveToolSelector("web_search_preview");
public sealed class LiveImageGenerationToolSelector() : LiveToolSelector("image_generation");
public sealed class LiveComputerToolSelector() : LiveToolSelector("computer");
public sealed class LiveCodeInterpreterToolSelector() : LiveToolSelector("code_interpreter");
public sealed class LiveProgrammaticToolSelector() : LiveToolSelector("programmatic_tool_calling");
public sealed class LiveShellToolSelector() : LiveToolSelector("shell");
public sealed class LiveApplyPatchToolSelector() : LiveToolSelector("apply_patch");

[JsonConverter(typeof(LiveResponsesToolJsonConverter))]
public abstract class LiveResponsesTool(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveFunctionTool() : LiveResponsesTool("function")
{
    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    [JsonPropertyName("strict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Strict { get; set; }
}

public sealed class LiveShellTool() : LiveResponsesTool("shell")
{
    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveShellEnvironment? Environment { get; set; }
}

public sealed class LiveWebSearchTool() : LiveResponsesTool("web_search");
public sealed class LiveFileSearchTool() : LiveResponsesTool("file_search");
public sealed class LiveCodeInterpreterTool() : LiveResponsesTool("code_interpreter");
public sealed class LiveImageGenerationTool() : LiveResponsesTool("image_generation");
public sealed class LiveMcpTool() : LiveResponsesTool("mcp");
public sealed class LiveCustomTool() : LiveResponsesTool("custom");
public sealed class LiveNamespaceTool() : LiveResponsesTool("namespace");
public sealed class LiveToolSearchTool() : LiveResponsesTool("tool_search");
public sealed class LiveProgrammaticTool() : LiveResponsesTool("programmatic_tool_calling");
public sealed class LiveComputerTool() : LiveResponsesTool("computer");
public sealed class LiveApplyPatchTool() : LiveResponsesTool("apply_patch");

[JsonConverter(typeof(LiveShellEnvironmentJsonConverter))]
public abstract class LiveShellEnvironment(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveContainerAutoEnvironment() : LiveShellEnvironment("container_auto")
{
    [JsonPropertyName("file_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FileIds { get; set; }

    [JsonPropertyName("memory_limit")]
    [LiveAllowedValues("1g", "4g", "16g", "64g")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemoryLimit { get; set; }

    [JsonPropertyName("network_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LiveNetworkPolicy? NetworkPolicy { get; set; }

    [JsonPropertyName("skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LiveSkill>? Skills { get; set; }
}

public sealed class LiveContainerReferenceEnvironment() : LiveShellEnvironment("container_reference")
{
    [Required]
    [JsonPropertyName("container_id")]
    public required string ContainerId { get; set; }
}

public sealed class LiveLocalEnvironment() : LiveShellEnvironment("local")
{
    [JsonPropertyName("skills")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LiveLocalSkill>? Skills { get; set; }
}

public sealed class LiveLocalSkill
{
    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; set; }
}

[JsonConverter(typeof(LiveNetworkPolicyJsonConverter))]
public abstract class LiveNetworkPolicy(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveDisabledNetworkPolicy() : LiveNetworkPolicy("disabled");

public sealed class LiveAllowlistNetworkPolicy() : LiveNetworkPolicy("allowlist")
{
    [Required]
    [JsonPropertyName("allowed_domains")]
    public required List<string> AllowedDomains { get; set; }
}

[JsonConverter(typeof(LiveSkillJsonConverter))]
public abstract class LiveSkill(string type)
{
    [JsonPropertyName("type")]
    public string Type { get; } = type;
}

public sealed class LiveSkillReference() : LiveSkill("skill_reference")
{
    [Required]
    [JsonPropertyName("skill_id")]
    public required string SkillId { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}

public sealed class LiveInlineSkill() : LiveSkill("inline")
{
    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("description")]
    public required string Description { get; set; }

    [Required]
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [Required]
    [JsonPropertyName("source")]
    public required LiveInlineSkillSource Source { get; set; }
}

public sealed class LiveInlineSkillSource
{
    [JsonPropertyName("type")]
    [AllowedValues("base64")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("media_type")]
    [AllowedValues("application/zip")]
    public string MediaType { get; set; } = "application/zip";

    [Required(AllowEmptyStrings = true)]
    [JsonPropertyName("data")]
    public required string Data { get; set; }
}
