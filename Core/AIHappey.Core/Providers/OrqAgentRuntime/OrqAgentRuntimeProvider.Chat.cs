using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrqAgentRuntime;

public partial class OrqAgentRuntimeProvider
{

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        ApplyAuthHeader();

        var emittedText = new Dictionary<string, string>(StringComparer.Ordinal);
        var emittedReasoning = new Dictionary<string, string>(StringComparer.Ordinal);
        var emittedToolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var startedTools = new HashSet<string>(StringComparer.Ordinal);
        var textId = $"text_{Guid.NewGuid():N}";
        var reasoningId = $"reasoning_{Guid.NewGuid():N}";
        bool textStarted = false;
        bool reasoningStarted = false;
        string finishReason = "stop";
        OrqInvokeResponse? lastChunk = null;
        string? errorText = null;

        yield return new StepStartUIPart();


        await foreach (var chunk in InvokeStreamingInternalAsync(BuildInvokeRequest(chatRequest, stream: true), cancellationToken))
        {
            lastChunk = chunk;

            foreach (var choice in chunk.Choices ?? [])
            {
                var choiceIndex = choice.Index;
                var text = ExtractMessageText(choice.Message);
                var textDelta = GetIncrementalDelta(emittedText, $"choice:{choiceIndex}:text", text);

                if (!string.IsNullOrEmpty(textDelta))
                {
                    if (!textStarted)
                    {
                        yield return new TextStartUIMessageStreamPart { Id = textId };
                        textStarted = true;
                    }

                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = textId,
                        Delta = textDelta
                    };
                }

                var reasoningDelta = GetIncrementalDelta(emittedReasoning, $"choice:{choiceIndex}:reasoning", choice.Message?.Reasoning);
                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    if (!reasoningStarted)
                    {
                        yield return new ReasoningStartUIPart { Id = reasoningId };
                        reasoningStarted = true;
                    }

                    yield return new ReasoningDeltaUIPart
                    {
                        Id = reasoningId,
                        Delta = reasoningDelta
                    };
                }

                foreach (var toolCall in choice.Message?.ToolCalls ?? [])
                {
                    var toolCallId = string.IsNullOrWhiteSpace(toolCall.Id)
                        ? $"tool_{Guid.NewGuid():N}"
                        : toolCall.Id!;
                    var toolName = toolCall.Function?.Name ?? toolCall.DisplayName ?? "tool";

                    if (startedTools.Add(toolCallId))
                    {
                        yield return ToolCallPart.CreateProviderExecuted(toolCallId, toolName, new { });
                    }

                    var argsDelta = GetIncrementalDelta(emittedToolArguments, toolCallId, toolCall.Function?.Arguments);
                    if (!string.IsNullOrEmpty(argsDelta))
                    {
                        yield return new ToolCallDeltaPart
                        {
                            ToolCallId = toolCallId,
                            InputTextDelta = argsDelta
                        };
                    }
                }

                if (!string.IsNullOrWhiteSpace(choice.FinishReason))
                    finishReason = choice.FinishReason!;
                else if (choice.Message?.ToolCalls?.Count > 0)
                    finishReason = "tool_calls";
            }

            if (chunk.IsFinal)
                finishReason = DetermineFinishReason(chunk, finishReason);
        }


        if (textStarted)
            yield return new TextEndUIMessageStreamPart { Id = textId };

        if (reasoningStarted)
            yield return new ReasoningEndUIPart { Id = reasoningId };

        if (lastChunk?.Retrievals?.Count > 0)
        {
            foreach (var retrieval in lastChunk.Retrievals)
            {
                var fileName = retrieval.Metadata?.FileName ?? "retrieval";
                var sourceId = !string.IsNullOrWhiteSpace(retrieval.Id)
                    ? retrieval.Id!
                    : $"retrieval_{Guid.NewGuid():N}";

                yield return new SourceDocumentPart
                {
                    SourceId = sourceId,
                    MediaType = retrieval.Metadata?.FileType ?? "text/plain",
                    Filename = fileName,
                    Title = retrieval.Metadata?.PageNumber is null ? fileName : $"{fileName} (page {retrieval.Metadata.PageNumber})",
                    ProviderMetadata = BuildRetrievalProviderMetadata(retrieval)
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(errorText))
            yield return new ErrorUIPart { ErrorText = errorText };

        yield return new FinishUIPart
        {
            FinishReason = finishReason,
            MessageMetadata = BuildUiMessageMetadata(lastChunk)
        };
    }


}
