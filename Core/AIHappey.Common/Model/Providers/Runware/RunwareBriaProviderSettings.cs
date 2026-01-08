using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Bria provider settings for Runware-hosted Bria models.
/// Mirrors Runware's <c>providerSettings.bria</c> object.
/// </summary>
public sealed class RunwareBriaProviderSettings
{
    [JsonPropertyName("promptEnhancement")]
    public bool? PromptEnhancement { get; set; }

    [JsonPropertyName("enhanceImage")]
    public bool? EnhanceImage { get; set; }

    /// <summary>
    /// e.g. "photography".
    /// </summary>
    [JsonPropertyName("medium")]
    public string? Medium { get; set; }

    [JsonPropertyName("promptContentModeration")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("contentModeration")]
    public bool? ContentModeration { get; set; }

    [JsonPropertyName("ipSignal")]
    public bool? IpSignal { get; set; }

    /// <summary>
    /// Pass-through for any future Bria settings we don't model yet.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

