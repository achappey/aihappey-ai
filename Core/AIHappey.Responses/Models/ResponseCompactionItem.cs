using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// Input or output item carrying opaque server-side compaction state for the Responses API.
/// </summary>
public sealed class ResponseCompactionItem : ResponseInputItem
{
    public ResponseCompactionItem()
    {
        Type = "compaction";
    }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("encrypted_content")]
    public string EncryptedContent { get; set; } = default!;
}
