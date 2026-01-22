using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ModernMT;

/// <summary>
/// Provider-specific options for the <c>modernmt</c> translator backend.
/// These are passed via:
/// - Vercel UI stream: <c>ChatRequest.providerMetadata.modernmt</c>
/// - MCP sampling: <c>CreateMessageRequestParams.metadata.modernmt</c>
/// </summary>
public sealed class ModernMTProviderMetadata
{
    /// <summary>
    /// The language code (ISO 639-1) of the source text(s).
    /// If omitted, ModernMT will attempt to detect the source language automatically.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// The context vector to use for the translation.
    /// </summary>
    [JsonPropertyName("context_vector")]
    public string? ContextVector { get; set; }

    /// <summary>
    /// The hints for the context vector (comma separated memory ids).
    /// </summary>
    [JsonPropertyName("hints")]
    public string? Hints { get; set; }

    /// <summary>
    /// The glossaries to use for the translation (comma separated memory ids).
    /// </summary>
    [JsonPropertyName("glossaries")]
    public string? Glossaries { get; set; }

    /// <summary>
    /// Apply specified glossaries case-insensitively.
    /// </summary>
    [JsonPropertyName("ignore_glossary_case")]
    public bool? IgnoreGlossaryCase { get; set; }

    /// <summary>
    /// Request priority. Can be <c>normal</c> or <c>background</c>.
    /// </summary>
    [JsonPropertyName("priority")]
    public string? Priority { get; set; }

    /// <summary>
    /// Force sentence splitting for long texts.
    /// </summary>
    [JsonPropertyName("multiline")]
    public bool? Multiline { get; set; }

    /// <summary>
    /// Abort request if not completed in the given milliseconds.
    /// </summary>
    [JsonPropertyName("timeout")]
    public int? Timeout { get; set; }

    /// <summary>
    /// Input text format. Options: <c>text/plain</c>, <c>text/xml</c>, <c>text/html</c>, <c>application/xliff+xml</c>.
    /// </summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>
    /// Include alternative translations. Max 32.
    /// </summary>
    [JsonPropertyName("alt_translations")]
    public int? AltTranslations { get; set; }

    /// <summary>
    /// Session identifier used for document-level adaptation.
    /// </summary>
    [JsonPropertyName("session")]
    public string? Session { get; set; }

    /// <summary>
    /// Detect and mask profanities in translation.
    /// </summary>
    [JsonPropertyName("mask_profanities")]
    public bool? MaskProfanities { get; set; }
}

