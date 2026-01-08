namespace AIHappey.Common.Model.Providers.Alibaba;

public sealed class AlibabaImageProviderMetadata
{
    /// <summary>
    /// DashScope negative prompt. Max 500 chars (provider constraint).
    /// </summary>
    public string? NegativePrompt { get; set; }

    /// <summary>
    /// Whether DashScope should rewrite/extend the prompt for creativity.
    /// Default: true (provider default).
    /// </summary>
    public bool? PromptExtend { get; set; }

    /// <summary>
    /// Add Qwen-Image watermark.
    /// Default: false (provider default).
    /// </summary>
    public bool? Watermark { get; set; }

    /// <summary>
    /// Provider size in DashScope format: "1664*928", "1472*1104", "1328*1328", "1104*1472", "928*1664".
    /// If provided, takes precedence over the request's generic Size.
    /// </summary>
    public string? Size { get; set; }

    /// <summary>
    /// Provider seed in range [0..2147483647].
    /// If provided, takes precedence over the request's generic Seed.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// Optional override for the DashScope base URL (eg https://dashscope-intl.aliyuncs.com).
    /// Not required for default operation.
    /// </summary>
    public string? BaseUrl { get; set; }
}

