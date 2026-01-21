using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Lingvanex;

/// <summary>
/// Provider-specific options for the <c>lingvanex</c> translator backend.
/// These are passed via:
/// - Vercel UI stream: <c>ChatRequest.providerMetadata.lingvanex</c>
/// - MCP sampling: <c>CreateMessageRequestParams.metadata.lingvanex</c>
/// </summary>
public sealed class LingvanexProviderMetadata
{
    /// <summary>
    /// Describe the input text format. Possible value is "html" for translating
    /// and preserving html structure. If value is not specified or is other than
    /// "html" then plain text is translated.
    /// </summary>
    [JsonPropertyName("translateMode")]
    public string? TranslateMode { get; set; }

    /// <summary>
    /// If true response includes sourceTransliteration and targetTransliteration fields.
    /// </summary>
    [JsonPropertyName("enableTransliteration")]
    public bool? EnableTransliteration { get; set; }
}

