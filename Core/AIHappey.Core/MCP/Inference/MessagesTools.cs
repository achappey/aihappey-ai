using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AIHappey.Core.MCP.Inference;

[McpServerToolType]
public class MessagesTools
{
    [Description("Execute an AI request using the Messages endpoint. MCP progress notifications contain accumulated text or thinking for each content block.")]
    [McpServerTool(
        Title = "AI Messages",
        Name = "ai_messages_execute",
        Destructive = false,
        UseStructuredContent = true,
        OutputSchemaType = typeof(MessagesResponse),
        Idempotent = false,
        ReadOnly = true,
        OpenWorld = false)]
    public static async Task<CallToolResult?> AIMessages_Execute(
        [Description("AI model identifier, including provider prefix.")] string model,
        [Description("Prompt to send to the model.")] string prompt,
        RequestContext<CallToolRequestParams> requestContext,
        IServiceProvider services,
        [Description("Optional system instructions to guide the model response.")] string? instructions = null,
        [Description("Optional maximum number of output tokens.")] int? maxOutputTokens = null,
        CancellationToken ct = default) =>
        await requestContext.WithExceptionCheck(async () =>
        {
            var (provider, providerModel) = await InferenceMcpHelpers.ResolveAsync(model, prompt, maxOutputTokens, services, ct);
            var request = new MessagesRequest
            {
                Model = providerModel,
                MaxTokens = maxOutputTokens,
                Stream = true,
                System = string.IsNullOrWhiteSpace(instructions) ? null : new MessagesContent(instructions),
                Messages = [new MessageParam { Role = "user", Content = new MessagesContent(prompt) }]
            };

            MessagesResponse? result = null;
            var blocks = new Dictionary<int, MessageContentBlock>();
            var buffers = new Dictionary<string, StringBuilder>();
            var progress = 1;

            await foreach (var part in provider.MessagesStreamingAsync(request, [], ct))
            {
                if (part.Message is not null)
                    result = part.Message;

                if (part.Type == "content_block_start" && part.Index is int startIndex && part.ContentBlock is not null)
                    blocks[startIndex] = part.ContentBlock;

                if (part.Type == "content_block_delta" && part.Index is int index && part.Delta is not null)
                {
                    if (!blocks.TryGetValue(index, out var block))
                        blocks[index] = block = new MessageContentBlock { Type = part.Delta.Type == "thinking_delta" ? "thinking" : "text" };

                    var delta = part.Delta.Text ?? part.Delta.Thinking;
                    var accumulated = InferenceMcpHelpers.Append(buffers, index.ToString(), delta);
                    if (part.Delta.Thinking is not null) block.Thinking = accumulated;
                    else block.Text = accumulated;
                    await InferenceMcpHelpers.SendProgressAsync(requestContext, progress++, accumulated);
                }

                if (part.Type == "message_delta")
                {
                    result ??= new MessagesResponse();
                    result.StopReason = part.Delta?.StopReason ?? result.StopReason;
                    result.StopSequence = part.Delta?.StopSequence ?? result.StopSequence;
                    result.Usage = part.Usage ?? result.Usage;
                }
            }

            if (result is null)
                throw new InvalidOperationException("The Messages stream completed without a 'message_start' event.");

            result.Content = blocks.OrderBy(x => x.Key).Select(x => x.Value).ToList();
            result.Model ??= providerModel;
            result.Role ??= "assistant";
            result.Type ??= "message";

            return new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(result, MessagesJson.Default) };
        });
}
