using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.xAI.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.xAI;

public static partial class XAIInputExtensions
{
    public static List<object> BuildResponsesInput(this List<UIMessage> uiMessages)
    {
        var items = new List<object>();

        foreach (var msg in uiMessages)
        {
            var contentBlocks = new List<IXAIMessageContent>();
            var toolItems = new List<object>(); // function_call + function_call_output items

            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    case FileUIPart file when
                        file.MediaType != null &&
                        file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                        file.Url != null &&
                        file.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase):
                        // Input image block (data URL)
                        contentBlocks.Add(new XAIImageUrlContent
                        {
                            Url = file.Url,
                            Detail = "high"
                        });
                        break;

                    case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                        contentBlocks.Add(text.Text.ToXAIMessageContent());
                        break;

                    case ToolInvocationPart tool:
                        // 1) function_call (top-level input item)
                        toolItems.Add(new XAIFunctionCall
                        {
                            Id = tool.ToolCallId,
                            CallId = tool.ToolCallId,
                            Name = tool.GetToolName(),
                            Status = "completed",
                            Arguments = JsonSerializer.Serialize(tool.Input, JsonSerializerOptions.Web)
                        });

                        // 2) function_call_output (top-level input item)
                        toolItems.Add(new XAIFunctionCallOutput
                        {
                            // must serialize as: { "type":"function_call_output", "call_id":..., "output":"{...}" }
                            CallId = tool.ToolCallId,
                            Output = JsonSerializer.Serialize(tool.Output, JsonSerializerOptions.Web)
                        });
                        break;
                }
            }

            // Emit the message item only if it has content blocks
            if (contentBlocks.Count > 0)
            {
                items.Add(new XAIMessage
                {
                    // must serialize as: { "type":"message", "role": "...", "content":[ ... ] }
                    Role = msg.Role.ToRole(),
                    Content = contentBlocks
                });
            }

            // Append the function_call / function_call_output items AFTER the message they relate to
            if (toolItems.Count > 0)
                items.AddRange(toolItems);
        }

        return items;
    }

    public static XAIMessageContent ToXAIMessageContent(
           this string text) => new()
           {
               Text = text
           };

    public static string ToRole(
               this Common.Model.Role role) => role switch
               {
                   Common.Model.Role.system => "system",
                   Common.Model.Role.user => "user",
                   Common.Model.Role.assistant => "assistant",
                   _ => "user"
               };

    public static string ToRole(
              this ModelContextProtocol.Protocol.Role role) => role == ModelContextProtocol.Protocol.Role.Assistant
               ? "assistant"
               : role == ModelContextProtocol.Protocol.Role.User
               ? "user" : "system";

    /// Builds xAI/OpenAI Responses API "input" from your UI messages.
    /// - Adds the system prompt (if any) as a system item with 'input_text'
    /// - Supports TextUIPart and FileUIPart (base64 data: image) via 'input_image'
    public static List<XAIMessage> BuildSamplingInput(
        this IList<SamplingMessage> uiMessages)
    {
        var items = new List<XAIMessage>();

        foreach (var msg in uiMessages)
        {
            foreach (var content in msg.Content)
            {
                var text = content is TextContentBlock textContentBlock ?
                    textContentBlock.Text
                    : content is EmbeddedResourceBlock embeddedResourceBlock && embeddedResourceBlock.Resource is TextResourceContents textResourceContents ?
                    textResourceContents.Text : null;

                if (!string.IsNullOrEmpty(text))
                    items.Add(new XAIMessage()
                    {
                        Role = msg.Role.ToRole(),
                        Content = [text.ToXAIMessageContent()]
                    });
            }
        }

        return items;
    }
}
