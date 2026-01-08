using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware Alibaba providerSettings.
/// </summary>
public sealed class RunwareAlibabaProviderSettings
{
    /// <summary>
    /// Enables prompt rewriting/extension.
    /// </summary>
    [JsonPropertyName("promptExtend")]
    public bool? PromptExtend { get; set; }
}

