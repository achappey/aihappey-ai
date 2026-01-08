using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware OpenAI providerSettings.
/// </summary>
public sealed class RunwareOpenAiProviderSettings
{
    /// <summary>
    /// DALL·E 3: hd|standard. GPT Image: auto|high|...
    /// </summary>
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    /// <summary>
    /// DALL·E 3: vivid|natural.
    /// </summary>
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    /// <summary>
    /// GPT Image: transparent|opaque.
    /// </summary>
    [JsonPropertyName("background")]
    public string? Background { get; set; }
}

