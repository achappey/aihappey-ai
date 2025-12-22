using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;
using AIHappey.Core.AI;
using AIHappey.Core.Providers.xAI.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.xAI;

public static partial class XAIExtensions
{
    public const string XAIIdentifier = "xai";

    public static XAIReasoning? ToReasoning(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("reasoning", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIReasoning>(webSearch.GetRawText());
    }

    public static XAIXCodeExecution? ToCodeExecution(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("code_execution", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIXCodeExecution>(webSearch.GetRawText());
    }

    public static XAIXSearch? ToXSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("x_search", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIXSearch>(webSearch.GetRawText());
    }


    public static XAIWebSearch? ToWebSearchTool(this JsonElement? element)
    {
        if (element == null) return null;

        if (!element.Value.TryGetProperty(XAIIdentifier, out var openai) || openai.ValueKind != JsonValueKind.Object)
            return null;

        if (!openai.TryGetProperty("web_search", out var webSearch) || webSearch.ValueKind != JsonValueKind.Object)
            return null;

        return JsonSerializer.Deserialize<XAIWebSearch>(webSearch.GetRawText());
    }


    public static List<dynamic> GetTools(this CreateMessageRequestParams chatRequest)
    {

        List<dynamic> allTools = [];
        XAIWebSearch? searchTool = chatRequest.Metadata.ToWebSearchTool();
        if (searchTool != null)
        {
            allTools.Add(searchTool);
        }

        XAIXSearch? xSearch = chatRequest.Metadata.ToXSearchTool();
        if (xSearch != null)
        {
            allTools.Add(xSearch);
        }

        XAIXCodeExecution? codeExecution = chatRequest.Metadata.ToCodeExecution();
        if (codeExecution != null)
        {
            allTools.Add(codeExecution);
        }

        return allTools;
    }


    public static Dictionary<string, object> ToProviderMetadata(this Dictionary<string, object> metadata)
        => new()
        { { XAIIdentifier, metadata } };


    /// Builds xAI/OpenAI Responses API "input" array from UI messages (stateless rebuild).
    /// - Message items => { type:"message", role, content:[ {input_text|output_text|input_image}... ] }
    /// - Tool calls    => { type:"function_call", id, call_id, name, arguments (JSON string), status }
    /// - Tool outputs  => { type:"function_call_output", call_id, output (JSON string) }
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
               this AIHappey.Common.Model.Role role) => role switch
               {
                   AIHappey.Common.Model.Role.system => "system",
                   AIHappey.Common.Model.Role.user => "user",
                   AIHappey.Common.Model.Role.assistant => "assistant",
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
