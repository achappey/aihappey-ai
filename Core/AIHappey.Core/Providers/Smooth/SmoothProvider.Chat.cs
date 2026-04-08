using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Smooth;

public partial class SmoothProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
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

                case ResponseOutputTextDone:
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

                    foreach (var filePart in ExtractFileUIPartsFromResponseOutput(completed.Response.Output))
                        yield return filePart;

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

                    yield return (failed.Response.Error?.Message ?? "Smooth task failed.").ToErrorUIPart();
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
