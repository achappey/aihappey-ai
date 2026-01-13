using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.MiniMax;

/// <summary>
/// ProviderOptions schema for MiniMax image generation.
/// Consumed via <c>providerOptions.minimax</c> for the unified <c>/v1/images/generations</c> flow.
/// </summary>
public sealed class MiniMaxImageProviderMetadata
{
    /// <summary>
    /// Enable MiniMax prompt optimizer.
    /// </summary>
    [JsonPropertyName("prompt_optimizer")]
    public bool? PromptOptimizer { get; set; }
}

