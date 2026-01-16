using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Responses.Streaming;

[JsonConverter(typeof(ResponseStreamConverter))]
public abstract class ResponseStreamPart
{
    public abstract string Type { get; init; }
}