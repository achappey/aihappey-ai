using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses;

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
        var request = new ResponseRequest
        {
            Model = chatRequest.Model,
            Temperature = chatRequest.Temperature,
            MaxOutputTokens = chatRequest.MaxOutputTokens,
            Text = chatRequest.ResponseFormat,
            Input = BuildPromptFromUiMessages(chatRequest.Messages),
            Stream = true
        };

        var textStarted = false;
        string? streamId = null;

        await foreach (var part in ExecuteResponsesStreamingAsync(request, cancellationToken))
        {
            switch (part)
            {
                case ResponseOutputTextDelta delta:
                    streamId ??= delta.ItemId;
                    if (!textStarted)
                    {
                        yield return streamId.ToTextStartUIMessageStreamPart();
                        textStarted = true;
                    }

                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = streamId,
                        Delta = delta.Delta
                    };
                    break;

                case ResponseOutputTextDone done:
                    if (!string.IsNullOrWhiteSpace(streamId) && textStarted)
                    {
                        yield return streamId!.ToTextEndUIMessageStreamPart();
                        textStarted = false;
                    }
                    break;

                case ResponseCompleted completed:
                    if (!string.IsNullOrWhiteSpace(streamId) && textStarted)
                    {
                        yield return streamId!.ToTextEndUIMessageStreamPart();
                        textStarted = false;
                    }

                    yield return "stop".ToFinishUIPart(
                        model: chatRequest.Model,
                        outputTokens: 0,
                        inputTokens: 0,
                        totalTokens: 0,
                        temperature: chatRequest.Temperature);
                    break;

                case ResponseFailed failed:
                    if (!string.IsNullOrWhiteSpace(streamId) && textStarted)
                    {
                        yield return streamId!.ToTextEndUIMessageStreamPart();
                        textStarted = false;
                    }

                    yield return (failed.Response.Error?.Message ?? "BrowserUse task failed.").ToErrorUIPart();
                    yield return "error".ToFinishUIPart(
                        model: chatRequest.Model,
                        outputTokens: 0,
                        inputTokens: 0,
                        totalTokens: 0,
                        temperature: chatRequest.Temperature);
                    break;
            }
        }
    }

}
