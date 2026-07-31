using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// A completed, provider-executed web search item that can be replayed as a
/// native Responses API input item. Only the native item fields are exposed so
/// transport/UI tool fields cannot accidentally leak into the request.
/// </summary>
public sealed class ResponseWebSearchCallItem : ResponseInputItem
{
    public ResponseWebSearchCallItem()
    {
        Type = "web_search_call";
    }

    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "completed";

    [JsonPropertyName("action")]
    public JsonElement Action { get; set; }
}
