
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses;


/// <summary>
/// type: "item_reference" (useful for passing prior items statelessly)
/// </summary>
public sealed class ResponseItemReference : ResponseInputItem
{
    public ResponseItemReference()
    {
        Type = "item_reference";
    }

    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;
}