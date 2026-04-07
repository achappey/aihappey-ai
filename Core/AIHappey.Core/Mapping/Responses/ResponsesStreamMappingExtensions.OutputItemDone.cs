using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

public static partial class ResponsesStreamMappingExtensions
{
    private static async IAsyncEnumerable<UIMessagePart> MapOutputItemDoneAsync(
       ResponseOutputItemDone outputItemDone,
       ResponsesStreamMappingContext context,
       [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var item = outputItemDone.Item;

        if (IsToolCallItemType(item.Type) && !context.PendingToolCalls.ContainsKey(item.Id ?? string.Empty))
        {
            var pending = CreatePendingToolCall(item, context);
            context.PendingToolCalls[pending.ItemId] = pending;
        }

        if (string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ToolOutputAvailablePart
            {
                ToolCallId = item.Id ?? $"mcp_call_{outputItemDone.OutputIndex}",
                ProviderExecuted = true,
                Output = new CallToolResult
                {
                    Content = [new TextContentBlock() {
                       Text = item.AdditionalProperties.TryGetString("output") ?? "No output"
                    }],
                }
            };
        }

        if (string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in CompleteToolCall(item.Id ?? string.Empty, item.Arguments ?? "{}", context))
                yield return part;
        }

        if (string.Equals(item.Type, "web_search_call", StringComparison.OrdinalIgnoreCase))
        {
            JsonElement? action = null;

            if (item.AdditionalProperties != null &&
                item.AdditionalProperties.TryGetValue("action", out var actionEl) &&
                actionEl.ValueKind == JsonValueKind.Object)
            {
                action = actionEl;
            }

            // extract fields
            JsonElement? queries = null;
            JsonElement? query = null;
            JsonElement? sources = null;

            if (action is { } a)
            {
                if (a.TryGetProperty("queries", out var q1))
                    queries = q1;

                if (a.TryGetProperty("query", out var q2))
                    query = q2;

                if (a.TryGetProperty("sources", out var q3))
                    sources = q3;
            }

            var input = new Dictionary<string, object?>();

            if (queries is not null)
                input["queries"] = queries.Value;

            if (query is not null)
                input["query"] = query.Value;

            yield return new ToolCallPart
            {
                ToolCallId = item.Id ?? $"web_search_{outputItemDone.OutputIndex}",
                ProviderExecuted = true,
                ToolName = "web_search",
                Input = input
            };

            yield return new ToolOutputAvailablePart
            {
                ToolCallId = item.Id ?? $"web_search_{outputItemDone.OutputIndex}",
                ProviderExecuted = true,
                Output = new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(new
                    {
                        sources
                    }, JsonSerializerOptions.Web)
                }

            };
        }

        if (string.Equals(item.Type, "file_search_call", StringComparison.OrdinalIgnoreCase))
        {

            yield return new ToolOutputAvailablePart
            {
                ToolCallId = item.Id ?? $"file_search_{outputItemDone.OutputIndex}",
                ProviderExecuted = true,
                Output = new CallToolResult
                {
                    StructuredContent = JsonSerializer.SerializeToElement(item.AdditionalProperties)
                    //  Content = ["Results not available".ToTextContentBlock()]
                }
            };
        }

        if (string.Equals(item.Type, "image_generation_call", StringComparison.OrdinalIgnoreCase))
        {
            var toolCallId = item.Id ?? $"image_generation_{outputItemDone.OutputIndex}";
            var partial = item.AdditionalProperties.TryGetString("result");
            var partialOutput = item.AdditionalProperties.TryGetString("output_format");

            if (!string.IsNullOrEmpty(partial) && !string.IsNullOrEmpty(partialOutput))
            {
                yield return new ToolOutputAvailablePart
                {
                    ToolCallId = toolCallId,
                    ProviderExecuted = true,
                    Output = new CallToolResult
                    {
                        Content = [ImageContentBlock.FromBytes(
                                Convert.FromBase64String(partial),
                                $"image/{partialOutput}"
                            )]
                    }
                };
            }
        }

        if (string.Equals(item.Type, "code_interpreter_call", StringComparison.OrdinalIgnoreCase))
        {
            var toolCallId = item.Id ?? $"code_interpreter_{outputItemDone.OutputIndex}";
            yield return BuildCodeInterpreterToolOutput(toolCallId, item.AdditionalProperties);
        }

        if (string.Equals(item.Type, "shell_call", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in CompleteShellCall(outputItemDone.OutputIndex, item, context))
                yield return part;
        }

        if (string.Equals(item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in CompleteShellCallOutput(outputItemDone.OutputIndex, item, context))
                yield return part;
        }

        if (context.Options.OutputItemDoneMapper != null)
        {
            await foreach (var part in context.Options.OutputItemDoneMapper(outputItemDone, context, cancellationToken))
                yield return part;
        }

        if (context.Options.OutputItemMapper != null)
        {
            await foreach (var part in context.Options.OutputItemMapper(item, cancellationToken))
                yield return part;
        }
    }

}
