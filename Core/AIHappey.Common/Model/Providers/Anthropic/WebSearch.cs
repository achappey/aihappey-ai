using System.Text.Json.Serialization;
using Anthropic.SDK.Messaging;

namespace AIHappey.Common.Model.Providers.Anthropic;

public sealed class WebSearch
{
    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; set; }

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("blocked_domains")]
    public List<string>? BlockedDomains { get; set; }

    [JsonPropertyName("user_location")]
    public UserLocation? UserLocation { get; set; }
}

