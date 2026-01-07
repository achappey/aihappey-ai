using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class MistralTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public IEnumerable<string>? TimestampGranularities { get; set; }
}

public class MistralProviderMetadata
{
    [JsonPropertyName("web_search")]
    public MistralWebSearch? WebSearch { get; set; }

    [JsonPropertyName("web_search_premium")]
    public MistralWebSearchPremium? WebSearchPremium { get; set; }

    [JsonPropertyName("code_interpreter")]
    public MistralCodeInterpreter? CodeInterpreter { get; set; }

    [JsonPropertyName("image_generation")]
    public MistralImageGeneration? ImageGeneration { get; set; }

    [JsonPropertyName("document_library")]
    public MistralDocumentLibrary? DocumentLibrary { get; set; }
}

public class MistralDocumentLibrary
{
    [JsonPropertyName("library_ids")]
    public List<string>? LibraryIds { get; set; }
}

public class MistralWebSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search";
}

public class MistralWebSearchPremium
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "web_search_premium";
}

public class MistralCodeInterpreter
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "code_interpreter";
}


public class MistralImageGeneration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image_generation";
}