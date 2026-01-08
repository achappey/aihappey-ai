using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiMcpTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "mcp";

    [JsonPropertyName("server_label")]
    public string ServerLabel { get; set; } = default!;

    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; set; }

    [JsonPropertyName("connector_id")]
    public string? ConnectorId { get; set; }

    [JsonPropertyName("require_approval")]
    public string RequireApproval { get; set; } = "never";

    [JsonPropertyName("allowed_tools")]
    public List<string> AllowedTools { get; set; } = [];

    [JsonPropertyName("authorization")]
    public string? Authorization { get; set; }
}

