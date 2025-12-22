
using System.Text.Json.Serialization;
using Anthropic.SDK.Messaging;

namespace AIHappey.Common.Model.Providers;

public class AnthropicProviderMetadata
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
}

public class Memory
{
}

public class CodeExecution
{
}

public class WebFetch
{
    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; set; }

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("blocked_domains")]
    public List<string>? BlockedDomains { get; set; }

    [JsonPropertyName("citations")]
    public Citations? Citations { get; set; }

    [JsonPropertyName("max_content_tokens")]
    public int? MaxContentTokens { get; set; }
}

public class WebSearch
{
    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; set; }

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("blocked_domains")]
    public List<string>? BlockedDomains { get; set; }

    [JsonPropertyName("user_location")]
    public Anthropic.SDK.Messaging.UserLocation? UserLocation { get; set; }
}
/*
public class UserLocation
{
    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }
}
*/