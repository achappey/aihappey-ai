using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

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

