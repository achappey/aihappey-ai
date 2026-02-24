using System.Text.Json;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Vercel.Models;
using System.Text.Json.Nodes;

namespace AIHappey.Core.Providers.Groq;

public static class GroqExtensions
{
    public static string Identifier() => nameof(Groq).ToLowerInvariant();

    public static IEnumerable<object> ToGroqMessages(this IEnumerable<UIMessage> messages)
    {
        foreach (var msg in messages)
        {
            var role = msg.Role.ToRole();

            List<object> content = [];
            List<dynamic> toolCalls = [];
            List<dynamic> toolResults = [];

            foreach (var part in msg.Parts)
            {
                if (part is TextUIPart textUIPart)
                {
                    content.Add(textUIPart.ToInputText());
                }
                else if (part is ToolInvocationPart toolInvocationPart)
                {
                    toolCalls.Add(new
                    {
                        call_id = toolInvocationPart.ToolCallId,
                        type = "function_call",
                        name = toolInvocationPart.GetToolName(),
                        arguments = JsonSerializer.Serialize(toolInvocationPart.Input)
                    });

                    toolResults.Add(new
                    {
                        call_id = toolInvocationPart.ToolCallId,
                        type = "function_call_output",
                        output = JsonSerializer.Serialize(toolInvocationPart.Output)
                    });
                }
            }

            if (content.Count > 0)
                yield return new { role, content };

            foreach (var result in toolResults)
            {
                var call = toolCalls.First(a => a.call_id == result.call_id);

                yield return call;
                yield return result;
            }


        }
    }

    public static object? ToCodeInterpreter(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[Identifier()] is not JsonObject provider)
            return null;

        if (provider["code_interpreter"] is not JsonObject codeInterpreter)
            return null;

        return codeInterpreter;
    }
    public static object? ToBrowserSearchTool(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[Identifier()] is not JsonObject provider)
            return null;

        if (provider["browser_search"] is not JsonObject browserSearch)
            return null;

        return browserSearch;
    }

    public static List<dynamic> GetTools(this CreateMessageRequestParams chatRequest)
    {

        List<dynamic> allTools = [];
        object? searchTool = chatRequest.Metadata.ToBrowserSearchTool();
        if (searchTool != null)
        {
            allTools.Add(searchTool);
        }

        object? xSearch = chatRequest.Metadata.ToCodeInterpreter();
        if (xSearch != null)
        {
            allTools.Add(xSearch);
        }

        return allTools;
    }

    public static object? ToReasoning(this JsonObject? obj)
    {
        if (obj is null)
            return null;

        if (obj[Identifier()] is not JsonObject provider)
            return null;

        if (provider["reasoning"] is not JsonObject reasoning)
            return null;

        return reasoning;
    }

    public static object[] BuildSamplingInput(this IEnumerable<SamplingMessage> messages)
    {
        return [.. messages
            .Where(m => !string.IsNullOrWhiteSpace(m?.ToText()))
            .Select(m => (object)new
            {
                role = m.Role.ToRole(),
                content = new object[]
                {
                    m.Content.FirstOrDefault()?.ToInputText()!
                }
            })];
    }

    private static object ToInputText(this ContentBlock contentBlock) =>
        new { type = "input_text", text = ((TextContentBlock)contentBlock).Text.Trim() };

    private static object ToInputText(this TextUIPart textUIPart) =>
        new { type = "input_text", text = textUIPart.Text.Trim() };

    private static string ToRole(this ModelContextProtocol.Protocol.Role role) => role switch
    {
        ModelContextProtocol.Protocol.Role.User => "user",
        ModelContextProtocol.Protocol.Role.Assistant => "assistant",
        _ => "user"
    };

    private static string ToRole(this Vercel.Models.Role role) => role switch
    {
        Vercel.Models.Role.system => "system",
        Vercel.Models.Role.assistant => "assistant",
        Vercel.Models.Role.user => "user",
        _ => "user"
    };

}
