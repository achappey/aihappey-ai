using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// type: "message"
/// </summary>
public sealed class ResponseInputMessage : ResponseInputItem
{
    public ResponseInputMessage()
    {
        Type = "message";
    }

    [JsonPropertyName("role")]
    public ResponseRole Role { get; set; } = ResponseRole.User;

    /// <summary>
    /// ✅ content: string OR array of parts
    /// </summary>
    [JsonPropertyName("content")]
    public ResponseMessageContent Content { get; set; } = new("");


    /// <summary>
    /// Optional (sometimes returned by API)
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Optional (sometimes returned by API)
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
