
using System.Text.Json.Serialization;

namespace AIHappey.Responses;

/// <summary>
/// ✅ input can be: string OR array of input items
/// </summary>
[JsonConverter(typeof(ResponseInputJsonConverter))]
public sealed class ResponseInput
{
    public string? Text { get; }
    public IReadOnlyList<ResponseInputItem>? Items { get; }

    public bool IsText => Text is not null;
    public bool IsItems => Items is not null;

    public ResponseInput(string text)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public ResponseInput(IEnumerable<ResponseInputItem> items)
    {
        Items = (items ?? throw new ArgumentNullException(nameof(items))).ToList();
    }

    public static implicit operator ResponseInput(string text) => new(text);
    public static implicit operator ResponseInput(List<ResponseInputItem> items) => new(items);
    public static implicit operator ResponseInput(ResponseInputItem[] items) => new(items);
}