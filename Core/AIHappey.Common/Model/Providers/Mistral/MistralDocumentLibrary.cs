using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Mistral;

public class MistralDocumentLibrary
{
    [JsonPropertyName("library_ids")]
    public List<string>? LibraryIds { get; set; }
}

