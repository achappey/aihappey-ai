using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Lara;

/// <summary>
/// Provider-specific translation options for Lara. Supply these through the
/// <c>lara</c> provider metadata object on a supported request surface.
/// </summary>
public sealed class LaraProviderMetadata
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sourceHint")]
    public string? SourceHint { get; set; }

    [JsonPropertyName("adaptTo")]
    public string[]? AdaptTo { get; set; }

    [JsonPropertyName("glossaries")]
    public string[]? Glossaries { get; set; }

    [JsonPropertyName("instructions")]
    public string[]? Instructions { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("reasoning")]
    public bool? Reasoning { get; set; }

    [JsonPropertyName("noTrace")]
    public bool? NoTrace { get; set; }

    [JsonPropertyName("timeoutInMillis")]
    public int? TimeoutInMillis { get; set; }

    [JsonPropertyName("imageModel")]
    public string? ImageModel { get; set; }
}
