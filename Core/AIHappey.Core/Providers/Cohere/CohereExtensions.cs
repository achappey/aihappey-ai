using AIHappey.Core.AI;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Cohere;

public static partial class CohereExtensions
{
    public const string CohereIdentifier = "cohere";

    public static string ToFinishReason(this string? finishReason) =>
        finishReason?.ToLowerInvariant() switch
        {
            null => "stop",
            "complete" => "stop",
            "max_tokens" => "length",
            "stop_sequence" => "stop",
            "tool_call" => "tool-calls",
            "error" => "error",
            "timeout" => "error",
            _ => "stop"
        };

    public static object? ToMessagePart(this UIMessagePart uiMessage)
    {
        if (uiMessage is TextUIPart textUIPart)
        {
            return textUIPart;
        }

        if (uiMessage is FileUIPart fileUIPart
            && fileUIPart.IsImage())
        {
            return new
            {
                type = "image_url",
                image_url = new
                {
                    url = fileUIPart.Url,
                    detail = "high"
                }
            };
        }

        return null;
    }

    public static IEnumerable<object> ToMessageParts(this List<UIMessagePart> uiMessages) =>
        uiMessages.Select(a => a.ToMessagePart()).OfType<object>();

    public static object ToMessage(this UIMessage uiMessage)
    {
        return new
        {
            role = uiMessage.Role,
            content = uiMessage.Parts.ToMessageParts()
        };
    }

    public static IEnumerable<object> ToMessages(this IEnumerable<UIMessage> uiMessages)
        => uiMessages.Select(a => a.ToMessage());

}
