using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.GreenPT;

public static class GreenPTCompletionsMappingExtensions
{
    public static IEnumerable<object> ToGreenPTMessages(this IEnumerable<ChatMessage> messages)
    {
        foreach (var msg in messages)
        {
            yield return new
            {
                role = msg.Role,
                content = msg.Content.ToGreenPTContentString()
            };
        }
    }

    public static string ToGreenPTContentString(this JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => content.GetRawText()
        };
    }
}

