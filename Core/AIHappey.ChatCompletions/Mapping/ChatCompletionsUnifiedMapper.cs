using System.Text.Json;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
}
