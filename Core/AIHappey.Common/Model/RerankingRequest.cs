using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

public class RerankingRequest
{

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("documents")]
    public IEnumerable<RerankingDocument> Documents { get; set; } = null!;

    [JsonPropertyName("query")]
    public string Query { get; set; } = null!;

    [JsonPropertyName("topN")]
    public int? TopN { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }
}

public class RerankingDocument
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("values")]
    public required JsonElement Values { get; init; }
}


public class RerankingResponse
{
    [JsonPropertyName("ranking")]
    public IEnumerable<RerankingRanking> Ranking { get; set; } = null!;

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("response")]
    public ResponseData Response { get; set; } = default!;
}

public class RerankingRanking
{
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("relevanceScore")]
    public required float RelevanceScore { get; init; }
}
