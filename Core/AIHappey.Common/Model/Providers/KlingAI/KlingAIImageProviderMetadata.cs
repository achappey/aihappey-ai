using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.KlingAI;

/// <summary>
/// ProviderOptions schema for KlingAI image generation.
/// Consumed via <c>providerOptions.klingai</c>.
/// </summary>
public sealed class KlingAIImageProviderMetadata
{
    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("image_reference")]
    public string? ImageReference { get; set; }

    [JsonPropertyName("image_fidelity")]
    public float? ImageFidelity { get; set; }

    [JsonPropertyName("human_fidelity")]
    public float? HumanFidelity { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; set; }

    [JsonPropertyName("external_task_id")]
    public string? ExternalTaskId { get; set; }
}
