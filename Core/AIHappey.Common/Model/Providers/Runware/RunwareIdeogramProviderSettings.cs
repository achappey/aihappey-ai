using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Runware;

/// <summary>
/// Runware Ideogram providerSettings.
/// Used for Runware-proxied Ideogram models (e.g. <c>ideogram:4@1</c>, <c>ideogram:4@2</c>).
/// </summary>
public sealed class RunwareIdeogramProviderSettings
{
    [JsonPropertyName("renderingSpeed")]
    public string? RenderingSpeed { get; set; }

    [JsonPropertyName("magicPrompt")]
    public string? MagicPrompt { get; set; }

    [JsonPropertyName("styleType")]
    public string? StyleType { get; set; }

    [JsonPropertyName("styleReferenceImages")]
    public IEnumerable<string>? StyleReferenceImages { get; set; }

    [JsonPropertyName("stylePreset")]
    public string? StylePreset { get; set; }

    [JsonPropertyName("characterReferenceImages")]
    public IEnumerable<string>? CharacterReferenceImages { get; set; }

    [JsonPropertyName("characterReferenceImagesMask")]
    public string? CharacterReferenceImagesMask { get; set; }

    // Remix-only (but safe to send only when set)
    [JsonPropertyName("remixStrength")]
    public int? RemixStrength { get; set; }

    [JsonPropertyName("styleCode")]
    public string? StyleCode { get; set; }
}

