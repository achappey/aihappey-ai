using System.Text.Json.Serialization;
using Anthropic.SDK.Messaging;

namespace AIHappey.Common.Model.Providers.Anthropic;

public sealed class WebFetch
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

