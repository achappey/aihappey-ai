using System.Text.Json.Serialization;
using Mscc.GenerativeAI;

namespace AIHappey.Common.Model.Providers.Google;

public class GoogleProviderMetadata
{
    public ThinkingConfig? ThinkingConfig { get; set; }

    public MediaResolution? MediaResolution { get; set; }

    [JsonPropertyName("google_search")]
    public GoogleSearch? GoogleSearch { get; set; }

    [JsonPropertyName("code_execution")]
    public Mscc.GenerativeAI.CodeExecution? CodeExecution { get; set; }

    [JsonPropertyName("url_context")]
    public UrlContext? UrlContext { get; set; }

    [JsonPropertyName("googleMaps")]
    public GoogleMaps? GoogleMaps { get; set; }

    [JsonPropertyName("toolConfig")]
    public ToolConfig? ToolConfig { get; set; }

    [JsonPropertyName("enableEnhancedCivicAnswers")]
    public bool? EnableEnhancedCivicAnswers { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }
}

