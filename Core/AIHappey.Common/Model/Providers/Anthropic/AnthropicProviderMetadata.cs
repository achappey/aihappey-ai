using System.Text.Json.Serialization;
using Anthropic.SDK.Messaging;

namespace AIHappey.Common.Model.Providers.Anthropic;

public sealed class AnthropicProviderMetadata
{
    [JsonPropertyName("code_execution")]
    public CodeExecution? CodeExecution { get; set; }

    [JsonPropertyName("container")]
    public Container? Container { get; set; }

    [JsonPropertyName("web_search")]
    public WebSearch? WebSearch { get; set; }

    [JsonPropertyName("web_fetch")]
    public WebFetch? WebFetch { get; set; }

    [JsonPropertyName("memory")]
    public Memory? Memory { get; set; }

    [JsonPropertyName("thinking")]
    public ThinkingParameters? Thinking { get; set; }

    [JsonPropertyName("mcp_servers")]
    public IEnumerable<MCPServer>? MCPServers { get; set; }

    [JsonPropertyName("anthropic-beta")]
    public IEnumerable<string>? AnthropicBeta { get; set; }


}

