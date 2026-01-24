
using System.Text.Json.Serialization;

namespace AIHappey.Responses;


[JsonConverter(typeof(ResponseMessageContentJsonConverter))]
public sealed class ResponseMessageContent
{
    public string? Text { get; }
    public IReadOnlyList<ResponseContentPart>? Parts { get; }

    public bool IsText => Text is not null;
    public bool IsParts => Parts is not null;

    public ResponseMessageContent(string text)
    {
        Text = text ?? "";
    }

    public ResponseMessageContent(IEnumerable<ResponseContentPart> parts)
    {
        Parts = (parts ?? throw new ArgumentNullException(nameof(parts))).ToList();
    }

    public static implicit operator ResponseMessageContent(string text) => new(text);
    public static implicit operator ResponseMessageContent(List<ResponseContentPart> parts) => new(parts);
    public static implicit operator ResponseMessageContent(ResponseContentPart[] parts) => new(parts);
}