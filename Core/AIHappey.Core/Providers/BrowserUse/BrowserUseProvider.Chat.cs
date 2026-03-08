using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteUiStreamingAsync(chatRequest, cancellationToken);
    }

    private async IAsyncEnumerable<UIMessagePart> ExecuteUiStreamingAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var createRequest = new BrowserUseCreateTaskRequest
        {
            Task = prompt,
            Llm = chatRequest.Model,
            MaxSteps = chatRequest.MaxOutputTokens ?? 100,
            StructuredOutput = TryExtractStructuredOutputSchemaString(chatRequest.ResponseFormat)
        };

        var textStarted = false;
        string? streamId = null;
        var emittedAnyText = false;

        await foreach (var evt in StreamNativeTaskAsync(createRequest, cancellationToken))
        {
            switch (evt)
            {
                case BrowserUseNativeCreatedStreamEvent created:
                    streamId = $"msg_{created.Created.Id}";
                    break;

                case BrowserUseNativeActionStreamEvent actionEvent:
                    streamId ??= $"msg_{actionEvent.Action.TaskId}";

                    var toolTitle = chatRequest.Tools?.FirstOrDefault(t => t.Name == actionEvent.Action.ToolName)?.Title;

                  /*  yield return new ToolCallStreamingStartPart
                    {
                        ToolCallId = actionEvent.Action.ToolCallId,
                        ToolName = actionEvent.Action.ToolName,
                        ProviderExecuted = true,
                        Title = toolTitle
                    };*/

                    yield return new ToolCallPart
                    {
                        ToolCallId = actionEvent.Action.ToolCallId,
                        ToolName = actionEvent.Action.ToolName,
                        Input = actionEvent.Action.Input,
                        ProviderExecuted = true,
                        Title = toolTitle
                    };

                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = actionEvent.Action.ToolCallId,
                        ProviderExecuted = true,
                        Output = actionEvent.Action.Output
                    };

                    if (actionEvent.Action.IsDone && !string.IsNullOrWhiteSpace(actionEvent.Action.DoneText))
                    {
                        emittedAnyText = true;

                        streamId ??= $"msg_{Guid.NewGuid():n}";
                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = actionEvent.Action.DoneText
                        };
                    }
                    break;

                case BrowserUseNativeTerminalStreamEvent terminalEvent:
                    streamId ??= $"msg_{terminalEvent.Terminal.Task.Id}";

                    if (!emittedAnyText && !string.IsNullOrWhiteSpace(terminalEvent.Terminal.OutputText))
                    {
                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = terminalEvent.Terminal.OutputText
                        };
                    }

                    if (textStarted && !string.IsNullOrWhiteSpace(streamId))
                        yield return streamId!.ToTextEndUIMessageStreamPart();

                    textStarted = false;

                    if (IsFinished(terminalEvent.Terminal.Status.Status))
                    {
                        yield return "stop".ToFinishUIPart(
                            model: chatRequest.Model,
                            outputTokens: 0,
                            inputTokens: 0,
                            totalTokens: 0,
                            temperature: chatRequest.Temperature);
                    }
                    else
                    {
                        var err = terminalEvent.Terminal.Status.Output;
                        if (!string.IsNullOrWhiteSpace(err))
                            yield return err!.ToErrorUIPart();

                        yield return "error".ToFinishUIPart(
                            model: chatRequest.Model,
                            outputTokens: 0,
                            inputTokens: 0,
                            totalTokens: 0,
                            temperature: chatRequest.Temperature);
                    }

                    yield break;
            }
        }
    }

}
