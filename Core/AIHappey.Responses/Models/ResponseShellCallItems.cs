using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// A provider-hosted shell invocation returned by, and replayed to, the
/// OpenAI Responses API. The item id (normally sh_...) and call id
/// (normally call_...) are separate identifiers and must both be preserved.
/// </summary>
public sealed class ResponseShellCallItem : ResponseInputItem
{
    public ResponseShellCallItem()
    {
        Type = "shell_call";
    }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("created_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("action")]
    public ResponseShellCallAction Action { get; set; } = new();

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(ResponseShellCallEnvironmentJsonConverter))]
    public ResponseShellCallEnvironment? Environment { get; set; }
}

public sealed class ResponseShellCallAction
{
    [JsonPropertyName("commands")]
    public List<string> Commands { get; set; } = [];

    [JsonPropertyName("timeout_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeoutMs { get; set; }

    [JsonPropertyName("max_output_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputLength { get; set; }
}

[JsonConverter(typeof(ResponseShellCallEnvironmentJsonConverter))]
public abstract class ResponseShellCallEnvironment
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ResponseShellLocalEnvironment : ResponseShellCallEnvironment
{
    [JsonPropertyName("type")]
    public override string Type => "local";
}

public sealed class ResponseShellContainerReferenceEnvironment : ResponseShellCallEnvironment
{
    [JsonPropertyName("type")]
    public override string Type => "container_reference";

    [JsonPropertyName("container_id")]
    public string ContainerId { get; set; } = default!;
}

/// <summary>
/// The result paired with a hosted shell invocation through call_id.
/// </summary>
public sealed class ResponseShellCallOutputItem : ResponseInputItem
{
    public ResponseShellCallOutputItem()
    {
        Type = "shell_call_output";
    }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("call_id")]
    public string CallId { get; set; } = default!;

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }

    [JsonPropertyName("created_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("max_output_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputLength { get; set; }

    [JsonPropertyName("output")]
    public List<ResponseShellCallOutputChunk> Output { get; set; } = [];
}

public sealed class ResponseShellCallOutputChunk
{
    [JsonPropertyName("stdout")]
    public string Stdout { get; set; } = string.Empty;

    [JsonPropertyName("stderr")]
    public string Stderr { get; set; } = string.Empty;

    [JsonPropertyName("created_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("outcome")]
    [JsonConverter(typeof(ResponseShellOutcomeJsonConverter))]
    public ResponseShellOutcome Outcome { get; set; } = new ResponseShellExitOutcome();
}

[JsonConverter(typeof(ResponseShellOutcomeJsonConverter))]
public abstract class ResponseShellOutcome
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed class ResponseShellExitOutcome : ResponseShellOutcome
{
    [JsonPropertyName("type")]
    public override string Type => "exit";

    [JsonPropertyName("exit_code")]
    public int ExitCode { get; set; }
}

public sealed class ResponseShellTimeoutOutcome : ResponseShellOutcome
{
    [JsonPropertyName("type")]
    public override string Type => "timeout";
}
