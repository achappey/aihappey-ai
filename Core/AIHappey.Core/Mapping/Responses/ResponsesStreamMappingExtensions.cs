using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.AI;

public static class ResponsesStreamMappingExtensions
{
    private const string CodeInterpreterToolName = "code_interpreter";

    public static async IAsyncEnumerable<UIMessagePart> ToUIMessagePartsAsync(
        this ResponseStreamPart update,
        string providerId,
        ResponsesStreamMappingContext? context = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        context ??= new ResponsesStreamMappingContext();
        var options = context.Options;

        switch (update)
        {
            case ResponseReasoningSummaryPartAdded responseReasoningSummaryPartAdded:
                yield return new ReasoningStartUIPart { Id = responseReasoningSummaryPartAdded.ItemId };
                yield break;
            case ResponseCreated:
                if (options.EmitStartStepOnCreated)
                    yield return new StartStepUIPart();
                yield break;

            case ResponseOutputItemAdded outputItemAdded:
                await foreach (var part in MapOutputItemAddedAsync(providerId, outputItemAdded, context, cancellationToken))
                    yield return part;
                yield break;

            case ResponseOutputItemDone outputItemDone:
                await foreach (var part in MapOutputItemDoneAsync(outputItemDone, context, cancellationToken))
                    yield return part;
                yield break;

            case ResponseOutputTextDelta outputTextDelta:
                if (options.StartTextOnDeltaIfMissing && context.StartedTextItemIds.Add(outputTextDelta.ItemId))
                    yield return outputTextDelta.ItemId.ToTextStartUIMessageStreamPart();

                context.TextDeltaItemIds.Add(outputTextDelta.ItemId);
                yield return new TextDeltaUIMessageStreamPart
                {
                    Id = outputTextDelta.ItemId,
                    Delta = outputTextDelta.Delta
                };
                yield break;

            case ResponseOutputTextDone outputTextDone:
                if (!context.TextDeltaItemIds.Contains(outputTextDone.ItemId)
                    && !string.IsNullOrWhiteSpace(outputTextDone.Text))
                {
                    if (context.StartedTextItemIds.Add(outputTextDone.ItemId))
                        yield return outputTextDone.ItemId.ToTextStartUIMessageStreamPart();

                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = outputTextDone.ItemId,
                        Delta = outputTextDone.Text
                    };
                }

                if (context.StartedTextItemIds.Contains(outputTextDone.ItemId))
                    yield return outputTextDone.ItemId.ToTextEndUIMessageStreamPart();

                if (TryCreateStructuredDataPart(outputTextDone.Text, options.StructuredOutputs, out var dataPart)
                    && dataPart != null)
                    yield return dataPart;

                yield break;

            case ResponseReasoningSummaryTextDelta reasoningSummaryDelta:
                yield return new ReasoningDeltaUIPart
                {
                    Id = reasoningSummaryDelta.ItemId,
                    Delta = reasoningSummaryDelta.Delta
                };
                yield break;
            case ResponseReasoningSummaryPartDone reasoningSummaryPartDone:
                yield return new ReasoningEndUIPart
                {
                    Id = reasoningSummaryPartDone.ItemId
                };
                yield break;

            case ResponseCodeInterpreterCallCodeDelta responseCodeInterpreterCallCodeDelta:

                yield return new ToolCallDeltaPart()
                {
                    ToolCallId = responseCodeInterpreterCallCodeDelta.ItemId,
                    InputTextDelta = responseCodeInterpreterCallCodeDelta.Delta
                };

                yield break;

            case ResponseShellCallCommandAdded shellCommandAdded:
                foreach (var part in StartShellCommand(shellCommandAdded, context))
                    yield return part;
                yield break;

            case ResponseShellCallCommandDelta shellCommandDelta:
                foreach (var part in AppendShellCommandDelta(shellCommandDelta, context))
                    yield return part;
                yield break;

            case ResponseShellCallCommandDone shellCommandDone:
                foreach (var part in CompleteShellCommand(shellCommandDone, context))
                    yield return part;
                yield break;

            case ResponseReasoningTextDelta reasoningTextDelta:
                if (context.StartedReasoningItemIds.Add(reasoningTextDelta.ItemId))
                    yield return new ReasoningStartUIPart { Id = reasoningTextDelta.ItemId };

                yield return new ReasoningDeltaUIPart
                {
                    Id = reasoningTextDelta.ItemId,
                    Delta = reasoningTextDelta.Delta
                };
                yield break;

            case ResponseReasoningTextDone reasoningTextDone:
                foreach (var part in CompleteReasoning(reasoningTextDone.ItemId, reasoningTextDone.Text, context, null))
                    yield return part;
                yield break;

            case ResponseFunctionCallArgumentsDelta functionDelta:
                foreach (var part in AppendToolCallDelta(functionDelta.ItemId, functionDelta.Delta, context))
                    yield return part;
                yield break;

            case ResponseMcpCallArgumentsDelta mcpDelta:
                foreach (var part in AppendToolCallDelta(mcpDelta.ItemId, mcpDelta.Delta, context))
                    yield return part;
                yield break;

            case ResponseFunctionCallArgumentsDone functionDone:
                foreach (var part in CompleteToolCall(functionDone.ItemId, functionDone.Arguments, context))
                    yield return part;
                yield break;

            case ResponseMcpCallArgumentsDone mcpDone:
                foreach (var part in CompleteToolCall(mcpDone.ItemId, mcpDone.Arguments, context))
                    yield return part;
                yield break;

            case ResponseOutputTextAnnotationAdded annotationAdded:
                await foreach (var part in MapAnnotationAsync(annotationAdded.Annotation, context, cancellationToken))
                    yield return part;
                yield break;

            case ResponseWebSearchCallInProgress webSearchInProgress:
                yield return ToolCallPart.CreateProviderExecuted(webSearchInProgress.ItemId, "web_search", new { });
                yield break;

            case ResponseWebSearchCallCompleted webSearchCompleted:
                yield return new ToolOutputAvailablePart
                {
                    ToolCallId = webSearchCompleted.ItemId,
                    ProviderExecuted = true,
                    Output = new { }
                };
                yield break;

            case ResponseCompleted completed:

                if (context.Options.BeforeFinishMapper != null)
                    await foreach (var part in context.Options.BeforeFinishMapper.Invoke(completed.Response, cancellationToken))
                        yield return part;

                context.ShellCalls.Clear();
                context.ShellCallIdsByItemId.Clear();
                context.ShellCallIdsByOutputIndex.Clear();

                yield return CreateFinishPart(providerId, completed.Response, options);
                yield break;

            case ResponseFailed failed:
                context.ShellCalls.Clear();
                context.ShellCallIdsByItemId.Clear();
                context.ShellCallIdsByOutputIndex.Clear();

                foreach (var part in options.FailedResponseFactory?.Invoke(failed.Response)
                    ?? DefaultFailureParts(providerId, failed.Response, options))
                {
                    yield return part;
                }

                yield break;

            case ResponseError error:
                context.ShellCalls.Clear();
                context.ShellCallIdsByItemId.Clear();
                context.ShellCallIdsByOutputIndex.Clear();
                yield return new ErrorUIPart { ErrorText = error.Message };
                yield break;

            case ResponseUnknownEvent unknown:
                await foreach (var part in MapUnknownEventAsync(unknown, context, cancellationToken))
                    yield return part;
                yield break;
        }
    }

    private static async IAsyncEnumerable<UIMessagePart> MapOutputItemAddedAsync(
        string providerId,
        ResponseOutputItemAdded outputItemAdded,
        ResponsesStreamMappingContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var item = outputItemAdded.Item;

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

        if (string.Equals(item.Type, "function_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Type, "mcp_call", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in CompleteToolCall(item.Id ?? string.Empty, item.Arguments ?? "{}", context))
                yield return part;
        }

        if (string.Equals(item.Type, "file_search_call", StringComparison.OrdinalIgnoreCase))
        {
            //var queries = item.AdditionalProperties.TryGetString("queries");

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

    private static IEnumerable<UIMessagePart> AppendToolCallDelta(string itemId, string delta, ResponsesStreamMappingContext context)
    {
        if (context.PendingToolCalls.TryGetValue(itemId, out var pending))
        {
            pending.Arguments.Append(delta);
            yield return delta.ToToolCallDeltaPart(pending.ToolCallId);
            yield break;
        }

        yield return delta.ToToolCallDeltaPart(itemId);
    }

    private static IEnumerable<UIMessagePart> CompleteToolCall(string itemId, string arguments, ResponsesStreamMappingContext context)
    {
        var pending = context.PendingToolCalls.TryGetValue(itemId, out var existing)
            ? existing
            : new ResponsesStreamMappingContext.PendingToolCall
            {
                ItemId = itemId,
                ToolCallId = itemId,
                ToolName = "unknown"
            };

        if (context.PendingToolCalls.ContainsKey(itemId))
            context.PendingToolCalls.Remove(itemId);

        var serializedArguments = pending.Arguments.Length > 0
            ? pending.Arguments.ToString()
            : arguments;

        yield return new ToolCallPart
        {
            ToolCallId = pending.ToolCallId,
            ToolName = pending.ToolName,
            ProviderExecuted = pending.ProviderExecuted ? true : null,
            Title = context.Options.ResolveToolTitle?.Invoke(pending.ToolName),
            Input = serializedArguments.DeserializeToObject()
        };

        if (!pending.ProviderExecuted && pending.RequireApproval)
        {
            yield return new ToolApprovalRequestPart
            {
                ToolCallId = pending.ToolCallId,
                ApprovalId = Guid.NewGuid().ToString()
            };
        }
    }

    private static IEnumerable<UIMessagePart> StartShellCommand(
        ResponseShellCallCommandAdded shellCommandAdded,
        ResponsesStreamMappingContext context)
    {
        var shellState = GetOrCreateShellCallState(context, shellCommandAdded.OutputIndex);
        var commandBuffer = GetOrCreateShellCommandBuffer(shellState, shellCommandAdded.CommandIndex);

        if (!string.IsNullOrEmpty(shellCommandAdded.Command))
        {
            commandBuffer.Clear();
            commandBuffer.Append(shellCommandAdded.Command);

            foreach (var part in EmitShellCommandProgress(shellState, context, shellCommandAdded.Command, shellCommandAdded.CommandIndex))
                yield return part;
        }
        else
        {
            foreach (var part in EmitShellCommandProgress(shellState, context, null, shellCommandAdded.CommandIndex))
                yield return part;
        }
    }

    private static IEnumerable<UIMessagePart> AppendShellCommandDelta(
        ResponseShellCallCommandDelta shellCommandDelta,
        ResponsesStreamMappingContext context)
    {
        var shellState = GetOrCreateShellCallState(context, shellCommandDelta.OutputIndex);
        var commandBuffer = GetOrCreateShellCommandBuffer(shellState, shellCommandDelta.CommandIndex);
        commandBuffer.Append(shellCommandDelta.Delta);

        foreach (var part in EmitShellCommandProgress(shellState, context, shellCommandDelta.Delta, shellCommandDelta.CommandIndex))
            yield return part;
    }

    private static IEnumerable<UIMessagePart> CompleteShellCommand(
        ResponseShellCallCommandDone shellCommandDone,
        ResponsesStreamMappingContext context)
    {
        var shellState = GetOrCreateShellCallState(context, shellCommandDone.OutputIndex);
        var commandBuffer = GetOrCreateShellCommandBuffer(shellState, shellCommandDone.CommandIndex);
        var emitInputDelta = commandBuffer.Length == 0 && !string.IsNullOrEmpty(shellCommandDone.Command);

        commandBuffer.Clear();
        commandBuffer.Append(shellCommandDone.Command);

        foreach (var part in EmitShellCommandProgress(
                     shellState,
                     context,
                     emitInputDelta ? shellCommandDone.Command : null,
                     shellCommandDone.CommandIndex))
        {
            yield return part;
        }

        if (shellState.CompletedCommandIndexes.Add(shellCommandDone.CommandIndex))
            yield return "\"".ToToolCallDeltaPart(shellState.ToolCallId);
    }

    private static IEnumerable<UIMessagePart> CompleteShellCall(
        int outputIndex,
        ResponseStreamItem item,
        ResponsesStreamMappingContext context)
    {
        var shellState = RegisterShellCallItem(outputIndex, item, context);

        foreach (var part in EnsureShellStreamStarted(shellState, context))
            yield return part;

        if (!shellState.InputCompleted)
        {
            foreach (var part in CompleteShellInputJson(shellState))
                yield return part;

            shellState.InputCompleted = true;
            var commands = GetShellCommands(item, shellState);

            yield return new ToolCallPart
            {
                ToolCallId = shellState.ToolCallId,
                ToolName = shellState.ToolName,
                ProviderExecuted = true,
                Title = context.Options.ResolveToolTitle?.Invoke(shellState.ToolName),
                Input = CreateShellToolInput(item.AdditionalProperties, commands)
            };
        }

        TryCleanupShellState(shellState.ToolCallId, context);
    }

    private static IEnumerable<UIMessagePart> CompleteShellCallOutput(
        int outputIndex,
        ResponseStreamItem item,
        ResponsesStreamMappingContext context)
    {
        var shellState = RegisterShellOutputItem(outputIndex, item, context);
        var outputText = BuildShellOutputText(item.AdditionalProperties);

        shellState.OutputCompleted = true;
        shellState.LastOutputPreview = outputText;

        if (!string.IsNullOrWhiteSpace(outputText))
            yield return CreateShellOutputPart(shellState.ToolCallId, outputText, preliminary: false);

        TryCleanupShellState(shellState.ToolCallId, context);
    }

    private static IEnumerable<UIMessagePart> CompleteReasoning(
        string itemId,
        string? text,
        ResponsesStreamMappingContext context,
        string? signature)
    {
        if (!string.IsNullOrWhiteSpace(text) && context.StartedReasoningItemIds.Add(itemId))
            yield return new ReasoningStartUIPart { Id = itemId };

        if (!string.IsNullOrWhiteSpace(text))
        {
            yield return new ReasoningDeltaUIPart
            {
                Id = itemId,
                Delta = text
            };
        }

        if (context.StartedReasoningItemIds.Contains(itemId))
        {
            yield return new ReasoningEndUIPart
            {
                Id = itemId
            };
        }
    }

    private static async IAsyncEnumerable<UIMessagePart> MapAnnotationAsync(
        ResponseStreamAnnotation annotation,
        ResponsesStreamMappingContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (context.Options.AnnotationMapper != null)
        {
            await foreach (var part in context.Options.AnnotationMapper(annotation, cancellationToken))
                yield return part;

            yield break;
        }

        var type = annotation.Type ?? annotation.AdditionalProperties.TryGetString("type");

        if (string.Equals(type, "url_citation", StringComparison.OrdinalIgnoreCase)
            || (string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(
                annotation.AdditionalProperties.TryGetString("url"))))
        {
            var url = annotation.AdditionalProperties.TryGetString("url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return new SourceUIPart
                {
                    Url = url,
                    Title = annotation.AdditionalProperties.TryGetString("title"),
                    SourceId = url
                };
            }

            yield break;
        }
    }

    private static async IAsyncEnumerable<UIMessagePart> MapUnknownEventAsync(
        ResponseUnknownEvent unknown,
        ResponsesStreamMappingContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (unknown.Type)
        {
            case "response.image_generation_call.partial_image":
                {
                    var partial = unknown.Data.TryGetString("partial_image_b64");
                    var partialOutput = unknown.Data.TryGetString("output_format");

                    if (!string.IsNullOrEmpty(partial) && !string.IsNullOrEmpty(partialOutput))
                    {
                        yield return new ToolOutputAvailablePart
                        {
                            ToolCallId = GetUnknownItemId(unknown),
                            ProviderExecuted = true,
                            Preliminary = true,
                            Output = new CallToolResult
                            {
                                Content = [ImageContentBlock.FromBytes(
                                Convert.FromBase64String(partial),
                                $"image/{partialOutput}"
                            )]
                            }
                        };
                    }

                    yield break;

                }

            case "response.image_generation_call.in_progress":
                yield return ToolCallPart.CreateProviderExecuted(GetUnknownItemId(unknown), "image_generation", new { });
                yield break;

            case "response.code_interpreter_call_code.done":
                {
                    var toolCallId = GetUnknownItemId(unknown);
                    var code = unknown.Data.TryGetString("code") ?? string.Empty;
                    yield return "\"}".ToToolCallDeltaPart(toolCallId);
                    yield return new ToolCallPart
                    {
                        ToolCallId = toolCallId,
                        ToolName = CodeInterpreterToolName,
                        ProviderExecuted = true,
                        Input = new { code }
                    };
                    yield break;
                }

                /*  case "response.shell_call_command.done":
                      {
                          var toolCallId = GetUnknownItemId(unknown);
                          var command = TryGetString(unknown.Data, "command") ?? string.Empty;
                          yield return "\"}".ToToolCallDeltaPart(toolCallId);
                          yield return new ToolCallPart
                          {
                              ToolCallId = toolCallId,
                              ToolName = "shell",
                              ProviderExecuted = true,
                              Input = new { command }
                          };
                          yield break;
                      }*/
        }

        if (context.Options.UnknownEventMapper != null)
        {
            await foreach (var part in context.Options.UnknownEventMapper(unknown, context, cancellationToken))
                yield return part;
        }

        if (context.Options.OutputItemMapper != null)
        {
            var syntheticItem = new ResponseStreamItem
            {
                Id = GetUnknownItemId(unknown),
                Type = unknown.Type,
                AdditionalProperties = unknown.Data
            };

            await foreach (var part in context.Options.OutputItemMapper(syntheticItem, cancellationToken))
                yield return part;
        }
    }

    private static ResponsesStreamMappingContext.PendingToolCall CreatePendingToolCall(
        ResponseStreamItem item,
        ResponsesStreamMappingContext context)
    {
        var toolName = item.Name
            ?? item.AdditionalProperties.TryGetString("name")
            ?? item.AdditionalProperties.TryGetString("tool_name")
            ?? item.Type;

        var toolCallId =
            item.Id ?? item.AdditionalProperties.TryGetString("call_id")
            ?? Guid.NewGuid().ToString("N");

        var providerExecuted = ResolveProviderExecuted(toolName, item, context.Options);
        var requireApproval = ResolveToolApproval(toolName, item, providerExecuted, context.Options);

        return new ResponsesStreamMappingContext.PendingToolCall
        {
            ItemId = item.Id ?? toolCallId,
            ToolCallId = toolCallId,
            ToolName = toolName,
            ProviderExecuted = providerExecuted,
            RequireApproval = requireApproval
        };
    }

    private static bool ResolveProviderExecuted(string toolName, ResponseStreamItem? item, ResponsesStreamMappingOptions options)
    {
        var resolved = options.ProviderExecutedResolver?.Invoke(toolName, item);
        if (resolved.HasValue)
            return resolved.Value;

        if (item != null && item.Type is "web_search_call" or "file_search_call" or "image_generation_call" or "code_interpreter_call")
            return true;

        return options.ProviderExecutedTools?.Contains(toolName, StringComparer.OrdinalIgnoreCase) == true;
    }

    private static bool ResolveToolApproval(string toolName, ResponseStreamItem? item, bool providerExecuted, ResponsesStreamMappingOptions options)
    {
        var resolved = options.ToolApprovalResolver?.Invoke(toolName, item);
        if (resolved.HasValue)
            return resolved.Value;

        var requireApproval = item?.AdditionalProperties.TryGetString("require_approval");
        if (!string.IsNullOrWhiteSpace(requireApproval))
            return !string.Equals(requireApproval, "never", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(requireApproval, "false", StringComparison.OrdinalIgnoreCase);

        return options.EmitToolApprovalRequests && !providerExecuted;
    }

    private static bool TryCreateStructuredDataPart(string text, object? structuredOutputs, out DataUIPart? part)
    {
        part = null;

        if (structuredOutputs == null || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var schema = structuredOutputs.GetJSONSchema();
            var data = JsonSerializer.Deserialize<object>(text, JsonSerializerOptions.Web);
            if (data == null)
                return false;

            part = new DataUIPart
            {
                Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                Data = data
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static FinishUIPart CreateFinishPart(string providerId, ResponseResult response, ResponsesStreamMappingOptions options)
    {
        if (options.FinishFactory != null)
            return options.FinishFactory(response);

        var usage = TryGetUsageValues(response.Usage);
        return "stop".ToFinishUIPart(
            response.Model.ToModelId(providerId),
            usage.outputTokens,
            usage.inputTokens,
            usage.totalTokens,
            response.Temperature,
            reasoningTokens: usage.reasoningTokens);
    }

    private static IEnumerable<UIMessagePart> DefaultFailureParts(string providerId, ResponseResult response, ResponsesStreamMappingOptions options)
    {
        if (!string.IsNullOrWhiteSpace(response.Error?.Message))
            yield return response.Error.Message.ToErrorUIPart();

        yield return CreateFinishPart(providerId, response, options);
    }

    private static (int inputTokens, int outputTokens, int totalTokens, int? reasoningTokens) TryGetUsageValues(object? usage)
    {
        if (usage == null)
            return default;

        try
        {
            var root = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            var inputTokens = root.TryGetNumber("input_tokens") ?? root.TryGetNumber("inputTokens") ?? 0;
            var outputTokens = root.TryGetNumber("output_tokens") ?? root.TryGetNumber("outputTokens") ?? 0;
            var totalTokens = root.TryGetNumber("total_tokens") ?? root.TryGetNumber("totalTokens")
                ?? (inputTokens + outputTokens);

            int? reasoningTokens = null;
            if (root.TryGetProperty("output_tokens_details", out var outputDetails))
                reasoningTokens = outputDetails.TryGetNumber("reasoning_tokens") ?? outputDetails.TryGetNumber("reasoningTokens");

            reasoningTokens ??= root.TryGetNumber("reasoning_tokens") ?? root.TryGetNumber("reasoningTokens");

            return (inputTokens, outputTokens, totalTokens, reasoningTokens);
        }
        catch
        {
            return default;
        }
    }

    private static ToolOutputAvailablePart BuildCodeInterpreterToolOutput(
        string toolCallId,
        Dictionary<string, JsonElement>? data)
    {
        List<ContentBlock> content = [];

        if (data != null && data.TryGetValue("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray())
            {
                var type = output.TryGetString("type");
                if (string.Equals(type, "logs", StringComparison.OrdinalIgnoreCase))
                {
                    var logs = output.TryGetString("logs");
                    if (!string.IsNullOrWhiteSpace(logs))
                        content.Add(logs.ToTextContentBlock());
                }
                else if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                {
                    var imageUri = output.TryGetString("image_url") ?? output.TryGetString("image_uri");
                    if (!string.IsNullOrWhiteSpace(imageUri))
                    {
                        content.Add(new ResourceLinkBlock
                        {
                            Name = Path.GetFileName(imageUri),
                            MimeType = "image/png",
                            Uri = imageUri
                        });
                    }
                }
            }
        }

        var containerId = data.TryGetString("container_id");
        if (!string.IsNullOrWhiteSpace(containerId))
            content.Add(JsonSerializer.Serialize(new { ContainerId = containerId }, JsonSerializerOptions.Web).ToTextContentBlock());

        return new ToolOutputAvailablePart
        {
            ToolCallId = toolCallId,
            ProviderExecuted = true,
            Output = new CallToolResult
            {
                Content = content.Count > 0
                    ? content
                    : ["Output not available".ToTextContentBlock()]
            }
        };
    }

    private static ResponsesStreamMappingContext.ShellCallState RegisterShellCallItem(
        int outputIndex,
        ResponseStreamItem item,
        ResponsesStreamMappingContext context)
    {
        var toolCallId = item.AdditionalProperties.TryGetString("call_id")
            ?? ResolveShellToolCallId(context, item.Id, outputIndex)
            ?? item.Id
            ?? $"shell_call_{outputIndex}";

        var shellState = GetOrCreateShellCallState(context, outputIndex, toolCallId, item.Id);
        shellState.ShellCallItemId = item.Id ?? shellState.ShellCallItemId;
        shellState.ShellCallOutputIndex = outputIndex;
        shellState.ToolName = "shell";
        return shellState;
    }

    private static ResponsesStreamMappingContext.ShellCallState RegisterShellOutputItem(
        int outputIndex,
        ResponseStreamItem item,
        ResponsesStreamMappingContext context)
    {
        var toolCallId = item.AdditionalProperties.TryGetString("call_id")
            ?? ResolveShellToolCallId(context, item.Id, outputIndex)
            ?? item.Id
            ?? $"shell_call_output_{outputIndex}";

        var shellState = GetOrCreateShellCallState(context, outputIndex, toolCallId, item.Id);
        shellState.ShellOutputItemId = item.Id ?? shellState.ShellOutputItemId;
        shellState.ShellOutputIndex = outputIndex;
        shellState.ToolName = "shell";
        return shellState;
    }

    private static ResponsesStreamMappingContext.ShellCallState GetOrCreateShellCallState(
        ResponsesStreamMappingContext context,
        int? outputIndex,
        string? preferredToolCallId = null,
        string? itemId = null)
    {
        string? existingToolCallId = null;

        if (!string.IsNullOrWhiteSpace(itemId)
            && context.ShellCallIdsByItemId.TryGetValue(itemId, out var itemMappedToolCallId))
        {
            existingToolCallId = itemMappedToolCallId;
        }
        else if (outputIndex.HasValue
                 && context.ShellCallIdsByOutputIndex.TryGetValue(outputIndex.Value, out var outputMappedToolCallId))
        {
            existingToolCallId = outputMappedToolCallId;
        }

        var resolvedToolCallId = preferredToolCallId
            ?? existingToolCallId
            ?? itemId
            ?? $"shell_call_{outputIndex ?? -1}";

        if (existingToolCallId != null
            && preferredToolCallId != null
            && !string.Equals(existingToolCallId, preferredToolCallId, StringComparison.Ordinal)
            && context.ShellCalls.TryGetValue(existingToolCallId, out var migratedState))
        {
            context.ShellCalls.Remove(existingToolCallId);
            migratedState.ToolCallId = preferredToolCallId;
            context.ShellCalls[preferredToolCallId] = migratedState;
            ReplaceShellToolCallIdReferences(context, existingToolCallId, preferredToolCallId);
            resolvedToolCallId = preferredToolCallId;
        }

        if (!context.ShellCalls.TryGetValue(resolvedToolCallId, out var shellState))
        {
            shellState = new ResponsesStreamMappingContext.ShellCallState
            {
                ToolCallId = resolvedToolCallId
            };
            context.ShellCalls[resolvedToolCallId] = shellState;
        }

        if (outputIndex.HasValue)
            context.ShellCallIdsByOutputIndex[outputIndex.Value] = shellState.ToolCallId;

        if (!string.IsNullOrWhiteSpace(itemId))
            context.ShellCallIdsByItemId[itemId] = shellState.ToolCallId;

        return shellState;
    }

    private static void ReplaceShellToolCallIdReferences(
        ResponsesStreamMappingContext context,
        string fromToolCallId,
        string toToolCallId)
    {
        foreach (var outputIndex in context.ShellCallIdsByOutputIndex
                     .Where(pair => string.Equals(pair.Value, fromToolCallId, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            context.ShellCallIdsByOutputIndex[outputIndex] = toToolCallId;
        }

        foreach (var itemId in context.ShellCallIdsByItemId
                     .Where(pair => string.Equals(pair.Value, fromToolCallId, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            context.ShellCallIdsByItemId[itemId] = toToolCallId;
        }
    }

    private static string? ResolveShellToolCallId(
        ResponsesStreamMappingContext context,
        string? itemId,
        int? outputIndex)
    {
        if (!string.IsNullOrWhiteSpace(itemId)
            && context.ShellCallIdsByItemId.TryGetValue(itemId, out var itemMappedToolCallId))
        {
            return itemMappedToolCallId;
        }

        if (outputIndex.HasValue
            && context.ShellCallIdsByOutputIndex.TryGetValue(outputIndex.Value, out var outputMappedToolCallId))
        {
            return outputMappedToolCallId;
        }

        return null;
    }

    private static StringBuilder GetOrCreateShellCommandBuffer(
        ResponsesStreamMappingContext.ShellCallState shellState,
        int commandIndex)
    {
        if (!shellState.CommandBuffers.TryGetValue(commandIndex, out var commandBuffer))
        {
            commandBuffer = new StringBuilder();
            shellState.CommandBuffers[commandIndex] = commandBuffer;
        }

        return commandBuffer;
    }

    private static IEnumerable<UIMessagePart> EnsureShellStreamStarted(
        ResponsesStreamMappingContext.ShellCallState shellState,
        ResponsesStreamMappingContext context)
    {
        if (shellState.StreamStarted)
            yield break;

        shellState.StreamStarted = true;
        yield return new ToolCallStreamingStartPart
        {
            ToolCallId = shellState.ToolCallId,
            ToolName = shellState.ToolName,
            ProviderExecuted = true,
            Title = context.Options.ResolveToolTitle?.Invoke(shellState.ToolName)
        };
    }

    private static IEnumerable<UIMessagePart> EmitShellCommandProgress(
        ResponsesStreamMappingContext.ShellCallState shellState,
        ResponsesStreamMappingContext context,
        string? delta,
        int? commandIndex = null)
    {
        foreach (var part in EnsureShellStreamStarted(shellState, context))
            yield return part;

        foreach (var part in StreamShellInputJsonDelta(shellState, delta, commandIndex))
            yield return part;

        var commandPreview = BuildShellCommandPreview(shellState);
        if (!string.IsNullOrWhiteSpace(commandPreview)
            && !string.Equals(commandPreview, shellState.LastCommandPreview, StringComparison.Ordinal))
        {
            shellState.LastCommandPreview = commandPreview;
            yield return CreateShellOutputPart(shellState.ToolCallId, commandPreview, preliminary: true);
        }
    }

    private static IEnumerable<UIMessagePart> CompleteShellInputJson(
        ResponsesStreamMappingContext.ShellCallState shellState)
    {
        if (!shellState.InputJsonStarted)
            yield break;

        if (shellState.StreamedCommandIndexes
            .Any(commandIndex => !shellState.CompletedCommandIndexes.Contains(commandIndex)))
        {
            yield return "\"".ToToolCallDeltaPart(shellState.ToolCallId);
        }

        yield return "]}".ToToolCallDeltaPart(shellState.ToolCallId);
    }

    private static string BuildShellCommandPreview(ResponsesStreamMappingContext.ShellCallState shellState)
        => string.Join(
            Environment.NewLine,
            shellState.CommandBuffers
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.ToString())
                .Where(command => !string.IsNullOrWhiteSpace(command)));

    private static IReadOnlyList<string> GetShellCommands(
        ResponseStreamItem item,
        ResponsesStreamMappingContext.ShellCallState shellState)
    {
        var bufferedCommands = shellState.CommandBuffers
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.ToString())
            .Where(command => !string.IsNullOrWhiteSpace(command))
            .ToList();

        if (bufferedCommands.Count > 0)
            return bufferedCommands;

        if (item.AdditionalProperties != null
            && item.AdditionalProperties.TryGetValue("action", out var actionElement)
            && actionElement.ValueKind == JsonValueKind.Object
            && actionElement.TryGetProperty("commands", out var commandsElement)
            && commandsElement.ValueKind == JsonValueKind.Array)
        {
            return commandsElement
                .EnumerateArray()
                .Where(command => command.ValueKind == JsonValueKind.String)
                .Select(command => command.GetString())
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .Select(command => command!)
                .ToList();
        }

        return [];
    }

    private static object CreateShellToolInput(
        Dictionary<string, JsonElement>? data,
        IReadOnlyList<string> commands)
    {
        Dictionary<string, object?> input = new(StringComparer.Ordinal)
        {
            ["commands"] = commands
        };

        if (data != null
            && data.TryGetValue("action", out var actionElement)
            && actionElement.ValueKind == JsonValueKind.Object)
        {
            if (actionElement.TryGetProperty("timeout_ms", out var timeoutElement)
                && timeoutElement.ValueKind == JsonValueKind.Number
                && timeoutElement.TryGetInt32(out var timeoutMs))
            {
                input["timeout_ms"] = timeoutMs;
            }

            if (actionElement.TryGetProperty("max_output_length", out var maxOutputLengthElement)
                && maxOutputLengthElement.ValueKind == JsonValueKind.Number
                && maxOutputLengthElement.TryGetInt32(out var maxOutputLength))
            {
                input["max_output_length"] = maxOutputLength;
            }
        }

        if (data != null
            && data.TryGetValue("environment", out var environmentElement)
            && environmentElement.ValueKind == JsonValueKind.Object)
        {
            input["environment"] = JsonSerializer.Deserialize<object>(environmentElement.GetRawText(), JsonSerializerOptions.Web);
        }

        return input;
    }

    private static IEnumerable<UIMessagePart> StreamShellInputJsonDelta(
        ResponsesStreamMappingContext.ShellCallState shellState,
        string? delta,
        int? commandIndex)
    {
        if (!shellState.InputJsonStarted)
        {
            shellState.InputJsonStarted = true;
            yield return "{\"commands\":[".ToToolCallDeltaPart(shellState.ToolCallId);
        }

        if (commandIndex.HasValue && shellState.StreamedCommandIndexes.Add(commandIndex.Value))
        {
            if (shellState.StreamedCommandIndexes.Count > 1)
                yield return ",".ToToolCallDeltaPart(shellState.ToolCallId);

            yield return "\"".ToToolCallDeltaPart(shellState.ToolCallId);
        }

        if (!string.IsNullOrEmpty(delta))
            yield return EscapeJsonFragment(delta).ToToolCallDeltaPart(shellState.ToolCallId);
    }

    private static string EscapeJsonFragment(string value)
    {
        var json = JsonSerializer.Serialize(value);
        return json[1..^1];
    }

    private static ToolOutputAvailablePart CreateShellOutputPart(
        string toolCallId,
        string outputText,
        bool preliminary)
        => new()
        {
            ToolCallId = toolCallId,
            ProviderExecuted = true,
            Preliminary = preliminary ? true : null,
            Output = new CallToolResult
            {
                IsError = false,
                Content = [outputText.ToTextContentBlock()]
            }
        };

    private static string BuildShellOutputText(Dictionary<string, JsonElement>? data)
    {
        if (data == null
            || !data.TryGetValue("output", out var outputElement)
            || outputElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var chunk in outputElement.EnumerateArray())
        {
            AppendShellOutputSegment(builder, chunk.TryGetString("stdout"));
            AppendShellOutputSegment(builder, chunk.TryGetString("stderr"));

            if (chunk.TryGetProperty("outcome", out var outcomeElement)
                && outcomeElement.ValueKind == JsonValueKind.Object)
            {
                var outcomeType = outcomeElement.TryGetString("type");
                if (string.Equals(outcomeType, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    var exitCode = outcomeElement.TryGetNumber("exit_code");
                    if (exitCode.HasValue)
                        AppendShellOutputSegment(builder, $"[exit_code: {exitCode.Value}]");
                }
                else if (string.Equals(outcomeType, "timeout", StringComparison.OrdinalIgnoreCase))
                {
                    AppendShellOutputSegment(builder, "[timeout]");
                }
            }
        }

        return builder.ToString();
    }

    private static void AppendShellOutputSegment(StringBuilder builder, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (builder.Length > 0)
            builder.AppendLine();

        builder.Append(text);
    }

    private static void TryCleanupShellState(string toolCallId, ResponsesStreamMappingContext context)
    {
        if (!context.ShellCalls.TryGetValue(toolCallId, out var shellState))
            return;

        if (!shellState.InputCompleted || !shellState.OutputCompleted)
            return;

        context.ShellCalls.Remove(toolCallId);

        foreach (var outputIndex in context.ShellCallIdsByOutputIndex
                     .Where(pair => string.Equals(pair.Value, toolCallId, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            context.ShellCallIdsByOutputIndex.Remove(outputIndex);
        }

        foreach (var itemId in context.ShellCallIdsByItemId
                     .Where(pair => string.Equals(pair.Value, toolCallId, StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            context.ShellCallIdsByItemId.Remove(itemId);
        }
    }

    private static bool IsToolCallItemType(string type)
        => string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase)
           || string.Equals(type, "mcp_call", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetReasoningText(ResponseStreamItem item)
    {
        if (item.Content?.Count > 0)
        {
            var text = item.Content
                .Where(part => string.Equals(part.Type, "summary_text", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(part.Type, "output_text", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(part.Type, "reasoning_text", StringComparison.OrdinalIgnoreCase))
                .Select(part => part.Text)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return item.AdditionalProperties.TryGetString("text")
            ?? item.AdditionalProperties.TryGetNestedString("summary", "text");
    }

    private static string GetUnknownItemId(ResponseUnknownEvent unknown)
        => unknown.Data.TryGetString("item_id")
           ?? unknown.Data.TryGetString("id")
           ?? $"unknown_{unknown.SequenceNumber ?? 0}";


}
