using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vapi;

public partial class VapiProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var responseRequest = ToResponseRequest(chatRequest);

        bool textStarted = false;
        bool finishSent = false;
        string currentTextId = "vapi-response";

        await foreach (var part in ResponsesStreamingAsyncInternal(responseRequest, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            switch (part)
            {
                case ResponseOutputTextDelta delta:
                    {
                        if (!string.IsNullOrWhiteSpace(delta.ItemId))
                            currentTextId = delta.ItemId;

                        if (!textStarted)
                        {
                            yield return currentTextId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        if (!string.IsNullOrEmpty(delta.Delta))
                        {
                            yield return new TextDeltaUIMessageStreamPart
                            {
                                Id = currentTextId,
                                Delta = delta.Delta
                            };
                        }

                        break;
                    }

                case ResponseOutputTextDone done:
                    {
                        if (!string.IsNullOrWhiteSpace(done.ItemId))
                            currentTextId = done.ItemId;

                        if (!textStarted)
                        {
                            yield return currentTextId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        if (textStarted)
                        {
                            yield return currentTextId.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }

                        break;
                    }

                case ResponseError error:
                    {
                        yield return new ErrorUIPart
                        {
                            ErrorText = error.Message
                        };
                        break;
                    }

                case ResponseFailed failed:
                    {
                        if (textStarted)
                        {
                            yield return currentTextId.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }

                        var failedModel = NormalizeIncomingModel(failed.Response?.Model, chatRequest.Model);
                        var (inputTokens, outputTokens, totalTokens, reasoningTokens) = ParseUsageTokens(failed.Response?.Usage);

                        yield return "error".ToFinishUIPart(
                            failedModel,
                            outputTokens,
                            inputTokens,
                            totalTokens,
                            chatRequest.Temperature,
                            reasoningTokens: reasoningTokens);

                        finishSent = true;
                        break;
                    }

                case ResponseCompleted completed:
                    {
                        if (textStarted)
                        {
                            yield return currentTextId.ToTextEndUIMessageStreamPart();
                            textStarted = false;
                        }

                        var responseModel = NormalizeIncomingModel(completed.Response?.Model, chatRequest.Model);
                        var (inputTokens, outputTokens, totalTokens, reasoningTokens) = ParseUsageTokens(completed.Response?.Usage);

                        yield return "stop".ToFinishUIPart(
                            responseModel,
                            outputTokens,
                            inputTokens,
                            totalTokens,
                            chatRequest.Temperature,
                            reasoningTokens: reasoningTokens);

                        finishSent = true;
                        break;
                    }
            }
        }

        if (textStarted)
            yield return currentTextId.ToTextEndUIMessageStreamPart();

        if (!finishSent)
        {
            yield return "stop".ToFinishUIPart(
                NormalizeIncomingModel(responseRequest.Model, chatRequest.Model),
                0,
                0,
                0,
                chatRequest.Temperature);
        }
    }

    private static ResponseRequest ToResponseRequest(ChatRequest chatRequest)
    {
        var inputItems = new List<ResponseInputItem>();

        foreach (var message in chatRequest.Messages)
        {
            List<ResponseContentPart> contentParts = [];
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextUIPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                        contentParts.Add(new InputTextPart(textPart.Text));
                        break;

                    case FileUIPart filePart
                        when filePart.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
                             && !string.IsNullOrWhiteSpace(filePart.Url):
                        contentParts.Add(new InputImagePart
                        {
                            ImageUrl = filePart.Url,
                            Detail = "auto"
                        });
                        break;
                }
            }

            if (contentParts.Count == 0)
                continue;

            inputItems.Add(new ResponseInputMessage
            {
                Role = message.Role switch
                {
                    Role.system => ResponseRole.System,
                    Role.assistant => ResponseRole.Assistant,
                    _ => ResponseRole.User
                },
                Content = contentParts
            });
        }

        return new ResponseRequest
        {
            Model = chatRequest.Model,
            Input = inputItems,
            Stream = true,
            Temperature = chatRequest.Temperature,
            TopP = chatRequest.TopP,
            MaxOutputTokens = chatRequest.MaxOutputTokens
        };
    }

    private static (int InputTokens, int OutputTokens, int TotalTokens, int? ReasoningTokens) ParseUsageTokens(object? usage)
    {
        if (usage is null)
            return (0, 0, 0, null);

        JsonElement usageElement;
        if (usage is JsonElement jsonElement)
        {
            usageElement = jsonElement;
        }
        else
        {
            try
            {
                usageElement = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);
            }
            catch
            {
                return (0, 0, 0, null);
            }
        }

        if (usageElement.ValueKind != JsonValueKind.Object)
            return (0, 0, 0, null);

        var inputTokens = usageElement.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var inTok)
            ? inTok
            : 0;
        var outputTokens = usageElement.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var outTok)
            ? outTok
            : 0;
        var totalTokens = usageElement.TryGetProperty("total_tokens", out var totalEl) && totalEl.TryGetInt32(out var totalTok)
            ? totalTok
            : 0;

        int? reasoningTokens = null;

        if (usageElement.TryGetProperty("reasoning_tokens", out var reasonEl) && reasonEl.TryGetInt32(out var reasonTok))
            reasoningTokens = reasonTok;
        else if (usageElement.TryGetProperty("output_tokens_details", out var detailsEl)
                 && detailsEl.ValueKind == JsonValueKind.Object
                 && detailsEl.TryGetProperty("reasoning_tokens", out var nestedReasonEl)
                 && nestedReasonEl.TryGetInt32(out var nestedReasonTok))
            reasoningTokens = nestedReasonTok;

        return (inputTokens, outputTokens, totalTokens, reasoningTokens);
    }
}

