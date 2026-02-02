using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.MiniMax;

/// <summary>
/// ProviderOptions schema for MiniMax video generation.
/// Consumed via <c>providerOptions.minimax</c> for the unified <c>/v1/video_generation</c> flow.
/// </summary>
public sealed class MiniMaxVideoProviderMetadata
{
    /// <summary>
    /// Whether to automatically optimize the prompt.
    /// </summary>
    [JsonPropertyName("prompt_optimizer")]
    public bool? PromptOptimizer { get; set; }

    /// <summary>
    /// Reduce optimization time when prompt optimizer is enabled.
    /// </summary>
    [JsonPropertyName("fast_pretreatment")]
    public bool? FastPretreatment { get; set; }
}
