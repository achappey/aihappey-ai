
using System.Text.Json;

namespace AIHappey.Common.Model.ChatCompletions;

public static class ChatMessageContentExtensions
{
    public static string? ToText(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => string.Concat(
                content.EnumerateArray()
                       .Where(p => p.TryGetProperty("type", out var t) && t.GetString() == "text")
                       .Select(p => p.TryGetProperty("text", out var txt) ? txt.GetString() : "")
            ),
            JsonValueKind.Null => null,
            _ => content.GetRawText() // fallback
        };
    }
}
