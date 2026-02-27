using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.LLMLayer;

public partial class LLMLayerProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return StreamUiInternalAsync(chatRequest, cancellationToken);
    }

    private async IAsyncEnumerable<UIMessagePart> StreamUiInternalAsync(
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
            Stream = true,
            Metadata = BuildUiMetadata(chatRequest)
        };

        var textStarted = false;
        string? streamId = null;

        await foreach (var part in ResponsesStreamingInternalAsync(request, cancellationToken))
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

                    var usage = completed.Response.Usage;
                    yield return "stop".ToFinishUIPart(
                        model: chatRequest.Model,
                        outputTokens: TryGetUsageInt(usage, "completion_tokens"),
                        inputTokens: TryGetUsageInt(usage, "prompt_tokens"),
                        totalTokens: TryGetUsageInt(usage, "total_tokens"),
                        temperature: chatRequest.Temperature);
                    break;

                case ResponseFailed failed:
                    if (!string.IsNullOrWhiteSpace(streamId) && textStarted)
                    {
                        yield return streamId!.ToTextEndUIMessageStreamPart();
                        textStarted = false;
                    }

                    yield return (failed.Response.Error?.Message ?? "LLMLayer stream failed.").ToErrorUIPart();
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

    private Dictionary<string, object?>? BuildUiMetadata(ChatRequest chatRequest)
    {
        var llmlayerMetadata = ExtractLlmlayerMetadata(chatRequest);
        if (llmlayerMetadata is null)
            return null;

        return new Dictionary<string, object?>
        {
            [GetIdentifier()] = llmlayerMetadata.Value
        };
    }

    private static int TryGetUsageInt(object? usage, string key)
    {
        if (usage is null)
            return 0;

        try
        {
            var root = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
                return 0;

            return el.TryGetInt32(out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }
}
