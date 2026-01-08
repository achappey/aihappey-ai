using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.OpenAI;

public sealed class OpenAiFileSearch
{
    [JsonPropertyName("vector_store_ids")]
    public List<string>? VectorStoreIds { get; set; }

    [JsonPropertyName("max_num_results")]
    public int? MaxNumResults { get; set; }
}

