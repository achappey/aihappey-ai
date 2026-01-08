using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware Midjourney providerSettings.
/// Used for Runware-proxied Midjourney models (e.g. <c>midjourney:1@1</c>, <c>midjourney:2@1</c>, <c>midjourney:3@1</c>).
/// </summary>
public sealed class RunwareMidjourneyProviderSettings
{
    /// <summary>
    /// "0.25" | "0.5" | "1" | "2".
    /// Note: Midjourney V7 supports only "1" and "2".
    /// </summary>
    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    /// <summary>
    /// 0..1000 (default 100).
    /// </summary>
    [JsonPropertyName("stylize")]
    public int? Stylize { get; set; }

    /// <summary>
    /// 0..100 (default 0).
    /// </summary>
    [JsonPropertyName("chaos")]
    public int? Chaos { get; set; }

    /// <summary>
    /// 0..3000 (default 0).
    /// </summary>
    [JsonPropertyName("weird")]
    public int? Weird { get; set; }

    /// <summary>
    /// "0" | "5" | "6" | "close".
    /// </summary>
    [JsonPropertyName("niji")]
    public string? Niji { get; set; }
}

