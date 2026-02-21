using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Infomaniak;

public sealed class InfomaniakTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }

    [JsonPropertyName("append_punctuations")]
    public string? AppendPunctuations { get; set; }

    [JsonPropertyName("prepend_punctuations")]
    public string? PrependPunctuations { get; set; }

    [JsonPropertyName("chunk_length")]
    public int? ChunkLength { get; set; }

    [JsonPropertyName("highlight_words")]
    public bool? HighlightWords { get; set; }

    [JsonPropertyName("max_line_count")]
    public int? MaxLineCount { get; set; }

    [JsonPropertyName("max_line_width")]
    public int? MaxLineWidth { get; set; }

    [JsonPropertyName("max_words_per_line")]
    public int? MaxWordsPerLine { get; set; }

    [JsonPropertyName("no_speech_threshold")]
    public double? NoSpeechThreshold { get; set; }
}

