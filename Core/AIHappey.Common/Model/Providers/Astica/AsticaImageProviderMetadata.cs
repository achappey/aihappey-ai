using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Astica;

/// <summary>
/// Optional provider options for Astica Design image generation.
/// Consumed via <c>providerOptions.astica</c>.
/// </summary>
public sealed class AsticaImageProviderMetadata
{
    /// <summary>
    /// Optional negative prompt. Maximum 350 characters.
    /// </summary>
    [JsonPropertyName("prompt_negative")]
    public string? PromptNegative { get; set; }

    /// <summary>
    /// Optional quality mode. Allowed values: high, standard, fast, faster.
    /// </summary>
    [JsonPropertyName("generate_quality")]
    public string? GenerateQuality { get; set; }

    /// <summary>
    /// Optional lossless output mode.
    /// 0 = JPG (default), 1 = PNG.
    /// </summary>
    [JsonPropertyName("generate_lossless")]
    public int? GenerateLossless { get; set; }

    /// <summary>
    /// Optional moderation flag.
    /// 1 = on, 0 = off (requires permission).
    /// </summary>
    [JsonPropertyName("moderate")]
    public int? Moderate { get; set; }

    /// <summary>
    /// Optional low-priority mode.
    /// 1 lowers cost and can be asynchronous.
    /// </summary>
    [JsonPropertyName("low_priority")]
    public int? LowPriority { get; set; }
}

