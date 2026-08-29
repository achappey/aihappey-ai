using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.PaxaLabs;

public sealed class PaxaLabsProviderMetadata
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("formality")]
    public string? Formality { get; set; }

    [JsonPropertyName("borrowed_words")]
    public string? BorrowedWords { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("alternatives")]
    public int? Alternatives { get; set; }

    [JsonPropertyName("do_not_translate")]
    public List<string>? DoNotTranslate { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("examples")]
    public List<PaxaLabsTranslationPair>? Examples { get; set; }

    [JsonPropertyName("glossary")]
    public List<PaxaLabsTranslationPair>? Glossary { get; set; }

    [JsonPropertyName("glossary_mode")]
    public string? GlossaryMode { get; set; }

    [JsonPropertyName("output")]
    public string? Output { get; set; }
}

public sealed class PaxaLabsTranslationPair
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = null!;

    [JsonPropertyName("target")]
    public string Target { get; set; } = null!;
}
