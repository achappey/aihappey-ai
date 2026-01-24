using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using Anthropic.SDK.Messaging;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{

    public static List<ContentBase> ToContentBase(this ModelContextProtocol.Protocol.CallToolResult tip)
    {
        if (tip is null) return [];

        var toolResultContent = new List<ContentBase>();

        if (tip.StructuredContent != null)
            toolResultContent.Add(tip.StructuredContent.ToJsonString().ToTextContent());

        foreach (var c in tip.Content)
        {
            switch (c)
            {
                case ModelContextProtocol.Protocol.TextContentBlock textBlock:
                    toolResultContent.Add(textBlock.Text.ToTextContent());
                    break;

                case ModelContextProtocol.Protocol.EmbeddedResourceBlock { Resource: ModelContextProtocol.Protocol.TextResourceContents textRes }:
                    toolResultContent.Add(textRes.Text.ToTextContent());
                    break;
            }
        }

        return toolResultContent;
    }


    public static ToolResultContent ToToolResultContent(this List<ContentBase> contentBases, string toolCallId, bool? isError)
        => new()
        {
            ToolUseId = toolCallId,
            IsError = isError,
            Content = contentBases,
        };


    public static Message? ToMessage(this ModelContextProtocol.Protocol.CallToolResult tip, string toolCallId)
    {
        var toolResultContent = tip.ToContentBase();
        if (toolResultContent.Count > 0)
        {
            var toolResult = toolResultContent.ToToolResultContent(toolCallId, tip.IsError);
            return toolResult.ToMessage(RoleType.User);
        }

        return null;
    }

    public static Message? ToToolResultMessage(this ToolInvocationPart tip)
    {
        if (tip.Output is null)
            return null;

        if (tip.Output is ModelContextProtocol.Protocol.CallToolResult callToolResponse)
            return callToolResponse.ToMessage(tip.ToolCallId);

        string serialized = tip.Output switch
        {
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(tip.Output, JsonSerializerOptions.Web)
        };

        var contentBase = serialized.ToTextContent();
        var resultContent = new List<ContentBase> { contentBase }
            .ToToolResultContent(tip.ToolCallId, null);

        return resultContent.ToMessage(RoleType.User);
    }


    public static ToolOutputAvailablePart ToToolOutputAvailablePart(this object result,
        string toolCallId,
        bool? providerExecuted = null)
        => new()
        {
            ToolCallId = toolCallId,
            Output = result,
            ProviderExecuted = providerExecuted
        };

    public static ToolOutputAvailablePart ToProviderToolOutputAvailablePart(this object result, string toolCallId)
        => result.ToToolOutputAvailablePart(toolCallId, true);

}
