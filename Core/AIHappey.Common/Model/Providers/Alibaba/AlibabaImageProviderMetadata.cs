namespace AIHappey.Common.Model.Providers.Alibaba;

/// <summary>
/// ProviderOptions schema for Alibaba (DashScope) image generation.
/// <para>
/// This is a provider-specific contract consumed via <c>providerOptions.alibaba</c>.
/// </para>
/// <para>
/// Models:
/// - Qwen-Image: configure via <c>providerOptions.alibaba.qwen</c>
/// - Tongyi Z-Image: configure via <c>providerOptions.alibaba.tongyi</c>
/// - Wan: reserved placeholder via <c>providerOptions.alibaba.wan</c>
/// </para>
/// </summary>
public sealed class AlibabaImageProviderMetadata
{
    public AlibabaQwenImageOptions? Qwen { get; set; }

    public AlibabaTongyiImageOptions? Tongyi { get; set; }

    public AlibabaWanImageOptions? Wan { get; set; }
}

public sealed class AlibabaQwenImageOptions
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
}

public sealed class AlibabaTongyiImageOptions
{
    /// <summary>
    /// Whether DashScope should rewrite/extend the prompt.
    /// Default: false (per Tongyi Z-Image docs).
    /// </summary>
    public bool? PromptExtend { get; set; }
}

/// <summary>
/// Wan (DashScope) image generation options.
/// <para>
/// Supports both <c>wan2.6-t2i</c> and <c>wan2.6-image</c>.
/// </para>
/// </summary>
public sealed class AlibabaWanImageOptions
{
    /// <summary>
    /// Controls Wan generation mode.
    /// <para>
    /// - <c>false</c>: image editing (requires 1-4 input images)
    /// - <c>true</c>: mixed text-and-image / text-to-image (0-1 input images)
    /// </para>
    /// </summary>
    public bool? EnableInterleave { get; set; }

    /// <summary>
    /// Whether DashScope should rewrite/extend the prompt.
    /// <para>
    /// Wan docs: applies to edit mode for <c>wan2.6-image</c> and for <c>wan2.6-t2i</c>.
    /// </para>
    /// </summary>
    public bool? PromptExtend { get; set; }

    /// <summary>
    /// Add watermark.
    /// </summary>
    public bool? Watermark { get; set; }

    /// <summary>
    /// Negative prompt.
    /// </summary>
    public string? NegativePrompt { get; set; }

    /// <summary>
    /// Max number of images for mixed text-and-image mode (<c>enable_interleave=true</c>).
    /// Provider range: 1-5.
    /// </summary>
    public int? MaxImages { get; set; }
}

