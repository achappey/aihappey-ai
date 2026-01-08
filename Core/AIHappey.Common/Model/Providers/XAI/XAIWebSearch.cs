using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIWebSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search";

    [JsonPropertyName("allowed_domains")]
    public List<string>? AllowedDomains { get; set; }

    [JsonPropertyName("excluded_domains")]
    public List<string>? ExcludedDomains { get; set; }

    [JsonPropertyName("enable_image_understanding")]
    public bool? EnableImageUnderstanding { get; set; }
}

