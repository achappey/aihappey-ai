using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static partial class ResponsesStreamMappingExtensions
{

    private static async IAsyncEnumerable<UIMessagePart> MapOutputItemAddedAsync(
        string providerId,
        ResponseOutputItemAdded outputItemAdded,
        ResponsesStreamMappingContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var item = outputItemAdded.Item;

        if (string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase))
        {
            object? argumentsValue = item.Arguments;

            if (!string.IsNullOrWhiteSpace(item.Arguments))
            {
                try
                {
                    using var doc = JsonDocument.Parse(item.Arguments);

                    // Clone nodig, anders hangt de JsonElement aan disposed JsonDocument
                    argumentsValue = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // gewone tekststring -> bestaand gedrag behouden
                    argumentsValue = new
                    {
                        arguments = item.Arguments
                    };
                }
            }

            yield return new ToolCallPart()
            {
                ToolCallId = item.Id!,
                ProviderExecuted = true,
                ToolName = $"{item.AdditionalProperties.TryGetString("server_label")} {item.Name}".Trim(),
                Input = argumentsValue ?? new { }
            };

            yield break;
        }

        if (string.Equals(item.Type, "shell_call_output", StringComparison.OrdinalIgnoreCase))
        {
            var shellState = RegisterShellOutputItem(outputItemAdded.OutputIndex, item, context);
            var outputText = BuildShellOutputText(item.AdditionalProperties);

            if (!string.IsNullOrWhiteSpace(outputText))
            {
                shellState.LastOutputPreview = outputText;
                yield return CreateShellOutputPart(shellState.ToolCallId, outputText, preliminary: true);
            }

            yield break;
        }

        if (string.Equals(item.Type, "shell_call", StringComparison.OrdinalIgnoreCase))
        {
            var shellState = RegisterShellCallItem(outputItemAdded.OutputIndex, item, context);
            foreach (var part in EnsureShellStreamStarted(shellState, context))
                yield return part;
            yield break;
        }

        if (string.Equals(item.Type, "code_interpreter_call", StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(item.Id)
                 && context.StartedTextItemIds.Add(item.Id))
        {
            yield return ToolCallStreamingStartPart.CreateProviderExecuted(item.Id, CodeInterpreterToolName);
            yield return $"{{ \"code\": \""
                    .ToToolCallDeltaPart(item.Id);
            yield break;
        }

        if (string.Equals(item.Type, "file_search_call", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(item.Id)
               && context.StartedTextItemIds.Add(item.Id))
        {
            yield return new ToolCallPart()
            {
                ToolCallId = item.Id,
                ProviderExecuted = true,
                ToolName = "file_search",
                Input = new { }
            };

            yield break;
        }

        if (string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(item.Id)
            && context.StartedTextItemIds.Add(item.Id))
        {
            var textStart = item.Id.ToTextStartUIMessageStreamPart(new Dictionary<string, object>()
            {
                [providerId] = new
                {
                    phase = item.Phase
                }
            });

            yield return textStart;

            foreach (var annotation in item.Content
                ?.Where(part => string.Equals(part.Type, "output_text", StringComparison.OrdinalIgnoreCase))
                .SelectMany(part => part.Annotations ?? []) ?? [])
            {
                await foreach (var mapped in MapAnnotationAsync(annotation, context, cancellationToken))
                    yield return mapped;
            }
        }

        if (IsToolCallItemType(item.Type))
        {
            var pending = CreatePendingToolCall(item, context);
            context.PendingToolCalls[pending.ItemId] = pending;

            yield return new ToolCallStreamingStartPart
            {
                ToolCallId = pending.ToolCallId,
                ToolName = pending.ToolName,
                ProviderExecuted = pending.ProviderExecuted ? true : null,
                Title = context.Options.ResolveToolTitle?.Invoke(pending.ToolName)
            };
        }
    }
}
