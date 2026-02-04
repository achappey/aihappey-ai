using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Alibaba;

/// <summary>
/// ProviderOptions schema for Alibaba (DashScope) video generation.
/// Consumed via <c>providerOptions.alibaba</c> for the unified video flow.
/// </summary>
public sealed class AlibabaVideoProviderMetadata
{
    [JsonPropertyName("wan")]
    public AlibabaWanVideoOptions? Wan { get; set; }
}

/// <summary>
/// Wan (DashScope) video generation options.
/// </summary>
public sealed class AlibabaWanVideoOptions
{
    /// <summary>
    /// Whether DashScope should rewrite/extend the prompt.
    /// </summary>
    [JsonPropertyName("prompt_extend")]
    public bool? PromptExtend { get; set; }

    /// <summary>
    /// Negative prompt.
    /// </summary>
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    /// <summary>
    /// Add watermark.
    /// </summary>
    [JsonPropertyName("watermark")]
    public bool? Watermark { get; set; }

    /// <summary>
    /// Shot type: single or multi (wan2.6 only; requires prompt_extend=true).
    /// </summary>
    [JsonPropertyName("shot_type")]
    public string? ShotType { get; set; }
}
