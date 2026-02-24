using System.Text.Json;
using AIHappey.Core.AI;
using Anthropic.SDK.Messaging;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Anthropic.Extensions;

public static partial class AnthropicExtensions
{

    public static IEnumerable<SystemMessage> ToSystemMessages(this List<UIMessage> messages) =>
        messages
            .Where(a => a.Role == Role.system)
            .SelectMany(a => a.Parts.OfType<TextUIPart>().Select(y => y.Text))
            .Select(a => new SystemMessage(a));

    public static IEnumerable<Message> ToMessages(this List<UIMessage> messages)
        => messages
            .Where(a => a.Role != Role.system)
            .SelectMany(a => a.ToMessages());

    public static IEnumerable<Message> ToMessages(this UIMessage message)
    {
        if (message.Role == Role.user)
        {
            yield return new Message
            {
                Role = RoleType.User,
                Content = [.. message.Parts.Select(a => a.ToContentBase()).OfType<ContentBase>()]
            };

            yield break;
        }

        var buffer = new List<ContentBase>();   // what we’ve collected so far

        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ReasoningUIPart reasoningUIPart:
                    if (!string.IsNullOrEmpty(reasoningUIPart.Text))
                    {
                        var signature = reasoningUIPart.ProviderMetadata.GetReasoningSignature(AnthropicConstants.AnthropicIdentifier);

                        if (!string.IsNullOrEmpty(signature))
                        {
                            buffer.Add(reasoningUIPart.Text.ToThinkingContent(signature));
                        }
                        else
                        {

                            buffer.Add(reasoningUIPart.Text.ToThinkingContent());
                        }
                    }

                    break;

                case TextUIPart textUIPart:
                    if (!string.IsNullOrEmpty(textUIPart.Text))
                        buffer.Add(textUIPart.Text.ToTextContent());
                    break;
                case ToolInvocationPart tip when
                    // PROVIDER-EXECUTED TOOLS (srvtoolu_ / mcptoolu_ / ProviderExecuted==true)
                    tip.ProviderExecuted == true
                    || tip.ToolCallId.StartsWith("srvtoolu_")
                    || tip.ToolCallId.StartsWith("mcptoolu_"):
                    {
                        if (tip.Output is not null)
                        {
                            var callToolResponse = JsonSerializer.Deserialize<
                                ModelContextProtocol.Protocol.CallToolResult>(
                                tip.Output.ToString()!, JsonSerializerOptions.Web);

                            if (callToolResponse is not null)
                            {
                                if (callToolResponse.StructuredContent != null)
                                {
                                    buffer.Add(callToolResponse.StructuredContent?.GetRawText().ToTextContent()!);
                                }

                                foreach (var c in callToolResponse.Content ?? [])
                                {
                                    if (c is ModelContextProtocol.Protocol.TextContentBlock tcb)
                                    {
                                        buffer.Add(tcb.Text.ToTextContent());
                                    }
                                    else if (c is ModelContextProtocol.Protocol.EmbeddedResourceBlock erb &&
                                             erb.Resource is ModelContextProtocol.Protocol.TextResourceContents trc)
                                    {
                                        buffer.Add(trc.Text.ToTextContent());
                                    }
                                }
                            }
                        }

                        break;
                    }

                case ToolInvocationPart tip:
                    {
                        // 👉 CLIENT TOOLS – deze wil Anthropic WEL zien als tool_use + tool_result

                        // a) ToolUseContent in de assistant-message
                        buffer.Add(new ToolUseContent
                        {
                            Id = tip.ToolCallId,
                            Input = JsonSerializer.SerializeToNode(tip.Input),
                            Name = tip.GetToolName()
                        });

                        // Flush de huidige assistant-buffer (text + tool_use)
                        if (buffer.Count > 0)
                        {
                            yield return new Message
                            {
                                Role = RoleType.Assistant,
                                Content = buffer
                            };
                            buffer = [];   // reset buffer
                        }

                        // b) Eventuele output als user/tool_result message
                        if (tip.Output is not null)
                        {
                            var messageItem = ToToolResultMessage(tip);
                            if (messageItem != null)
                            {
                                yield return messageItem;
                            }
                        }

                        break;
                    }
            }
        }

        // -----------------------------------------------------------------
        // Anything still buffered (e.g. trailing text) becomes the last
        // assistant message
        // -----------------------------------------------------------------
        if (buffer.Count > 0)
        {
            yield return new Message
            {
                Role = RoleType.Assistant,
                Content = buffer
            };
        }
    }

}
