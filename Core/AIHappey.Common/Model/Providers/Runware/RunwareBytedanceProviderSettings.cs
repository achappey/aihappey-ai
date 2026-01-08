using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware ByteDance providerSettings.
/// Used for Runware-proxied ByteDance models (e.g. <c>bytedance:5@0</c>, <c>bytedance:seedream@4.5</c>).
/// </summary>
public sealed class RunwareBytedanceProviderSettings
{
    /// <summary>
    /// 1..15. Enables sequential image generation for storyboard / comic workflows.
    /// Note: reference images + sequential images cannot exceed 15.
    /// </summary>
    [JsonPropertyName("maxSequentialImages")]
    public int? MaxSequentialImages { get; set; }

    /// <summary>
    /// "standard" | "fast".
    /// Controls prompt optimization mode.
    /// </summary>
    [JsonPropertyName("optimizePromptMode")]
    public string? OptimizePromptMode { get; set; }
}

