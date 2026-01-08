using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware <c>providerSettings</c> container.
/// We support multiple nested provider configs (like AIML & Alibaba patterns).
/// </summary>
public sealed class RunwareProviderSettings
{
    /// <summary>
    /// OpenAI settings for Runware-hosted OpenAI image models.
    /// </summary>
    [JsonPropertyName("openai")]
    public RunwareOpenAiProviderSettings? OpenAI { get; set; }

    /// <summary>
    /// Google settings for Runware-hosted Google models (Imagen/Veo).
    /// Only image-related settings are relevant for this provider implementation.
    /// </summary>
    [JsonPropertyName("google")]
    public RunwareGoogleProviderSettings? Google { get; set; }

    /// <summary>
    /// Runway settings for Runware-hosted Runway models.
    /// </summary>
    [JsonPropertyName("runway")]
    public RunwareRunwayProviderSettings? Runway { get; set; }

    /// <summary>
    /// Midjourney settings for Runware-hosted Midjourney image models.
    /// </summary>
    [JsonPropertyName("midjourney")]
    public RunwareMidjourneyProviderSettings? Midjourney { get; set; }

    /// <summary>
    /// Alibaba settings for Runware-hosted Alibaba models (Wan).
    /// </summary>
    [JsonPropertyName("alibaba")]
    public RunwareAlibabaProviderSettings? Alibaba { get; set; }

    /// <summary>
    /// PrunaAI settings for Runware-hosted PrunaAI models.
    /// </summary>
    [JsonPropertyName("prunaai")]
    public RunwarePrunaAiProviderSettings? PrunaAi { get; set; }

    /// <summary>
    /// Ideogram settings for Runware-hosted Ideogram image models.
    /// </summary>
    [JsonPropertyName("ideogram")]
    public RunwareIdeogramProviderSettings? Ideogram { get; set; }

    /// <summary>
    /// ByteDance settings for Runware-hosted ByteDance models (SeedEdit / Seedream).
    /// </summary>
    [JsonPropertyName("bytedance")]
    public RunwareBytedanceProviderSettings? Bytedance { get; set; }

    /// <summary>
    /// Bria settings for Runware-hosted Bria models (Bria 3.2 / FIBO).
    /// </summary>
    [JsonPropertyName("bria")]
    public RunwareBriaProviderSettings? Bria { get; set; }
}


