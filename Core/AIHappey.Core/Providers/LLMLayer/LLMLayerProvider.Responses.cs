using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.LLMLayer;

public partial class LLMLayerProvider
{
    private async Task<ResponseResult> ResponsesInternalAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("LLMLayer requires non-empty input.");

        var payload = BuildAnswerPayload(
            query: prompt,
            model: options.Model ?? string.Empty,
            temperature: options.Temperature,
            maxTokens: options.MaxOutputTokens,
            llmlayerMetadata: ExtractLlmlayerMetadata(options.Metadata));

        ApplyStructuredOutputIfAny(payload, options.Text);

        var answer = await ExecuteAnswerAsync(payload, cancellationToken);
        var text = AnswerToText(answer.Answer);

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = id,
            Object = "response",
            CreatedAt = created,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = "completed",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = BuildResponseMetadata(options.Metadata, answer),
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Usage = BuildUsage(answer),
            Output =
            [
                new
                {
                    id = $"msg_{id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingInternalAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("LLMLayer requires non-empty input.");

        var payload = BuildAnswerPayload(
            query: prompt,
            model: options.Model ?? string.Empty,
            temperature: options.Temperature,
            maxTokens: options.MaxOutputTokens,
            llmlayerMetadata: ExtractLlmlayerMetadata(options.Metadata));

        ApplyStructuredOutputIfAny(payload, options.Text);

        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;

        var output = new System.Text.StringBuilder();
        JsonElement sources = default;
        JsonElement images = default;
        string? responseTime = null;
        int? inputTokens = null;
        int? outputTokens = null;
        decimal? modelCost = null;
        decimal? llmlayerCost = null;

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = created,
            Status = "in_progress",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        await foreach (var evt in ExecuteAnswerStreamingAsync(payload, cancellationToken))
        {
            switch (evt.Type)
            {
                case "sources":
                    if (evt.Root.TryGetProperty("data", out var sourceData) && sourceData.ValueKind == JsonValueKind.Array)
                        sources = sourceData.Clone();
                    break;

                case "images":
                    if (evt.Root.TryGetProperty("data", out var imageData) && imageData.ValueKind == JsonValueKind.Array)
                        images = imageData.Clone();
                    break;

                case "answer":
                    if (evt.Root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var delta = contentEl.GetString() ?? string.Empty;
                        if (delta.Length > 0)
                        {
                            output.Append(delta);
                            yield return new ResponseOutputTextDelta
                            {
                                SequenceNumber = sequence++,
                                ItemId = itemId,
                                Outputindex = 0,
                                ContentIndex = 0,
                                Delta = delta
                            };
                        }
                    }
                    break;

                case "usage":
                    inputTokens = evt.Root.TryGetProperty("input_tokens", out var inTokEl) && inTokEl.TryGetInt32(out var inTok)
                        ? inTok
                        : inputTokens;
                    outputTokens = evt.Root.TryGetProperty("output_tokens", out var outTokEl) && outTokEl.TryGetInt32(out var outTok)
                        ? outTok
                        : outputTokens;
                    modelCost = evt.Root.TryGetProperty("model_cost", out var mcEl) && mcEl.TryGetDecimal(out var mc)
                        ? mc
                        : modelCost;
                    llmlayerCost = evt.Root.TryGetProperty("llmlayer_cost", out var lcEl) && lcEl.TryGetDecimal(out var lc)
                        ? lc
                        : llmlayerCost;
                    break;

                case "done":
                    responseTime = evt.Root.TryGetProperty("response_time", out var rtEl) && rtEl.ValueKind == JsonValueKind.String
                        ? rtEl.GetString()
                        : responseTime;

                    var answerResponse = new LLMLayerAnswerResponse
                    {
                        Answer = JsonSerializer.SerializeToElement(output.ToString(), JsonWeb),
                        Sources = sources,
                        Images = images,
                        ResponseTime = responseTime,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        ModelCost = modelCost,
                        LlmlayerCost = llmlayerCost
                    };

                    var text = output.ToString();
                    var finalResult = new ResponseResult
                    {
                        Id = responseId,
                        Object = "response",
                        CreatedAt = created,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Status = "completed",
                        Model = options.Model ?? string.Empty,
                        Temperature = options.Temperature,
                        Metadata = BuildResponseMetadata(options.Metadata, answerResponse),
                        MaxOutputTokens = options.MaxOutputTokens,
                        Store = options.Store,
                        ToolChoice = options.ToolChoice,
                        Tools = options.Tools?.Cast<object>() ?? [],
                        Text = options.Text,
                        ParallelToolCalls = options.ParallelToolCalls,
                        Usage = BuildUsage(answerResponse),
                        Output =
                        [
                            new
                            {
                                id = itemId,
                                type = "message",
                                role = "assistant",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "output_text",
                                        text
                                    }
                                }
                            }
                        ]
                    };

                    yield return new ResponseOutputTextDone
                    {
                        SequenceNumber = sequence++,
                        ItemId = itemId,
                        Outputindex = 0,
                        ContentIndex = 0,
                        Text = text
                    };

                    yield return new ResponseCompleted
                    {
                        SequenceNumber = sequence,
                        Response = finalResult
                    };
                    yield break;

                case "error":
                    var message = evt.Root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                        ? msgEl.GetString()
                        : "LLMLayer stream returned an error event.";

                    var failedResult = new ResponseResult
                    {
                        Id = responseId,
                        Object = "response",
                        CreatedAt = created,
                        CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Status = "failed",
                        Model = options.Model ?? string.Empty,
                        Temperature = options.Temperature,
                        Metadata = options.Metadata,
                        MaxOutputTokens = options.MaxOutputTokens,
                        Store = options.Store,
                        ToolChoice = options.ToolChoice,
                        Tools = options.Tools?.Cast<object>() ?? [],
                        Text = options.Text,
                        ParallelToolCalls = options.ParallelToolCalls,
                        Error = new ResponseResultError
                        {
                            Code = "llmlayer_stream_error",
                            Message = message
                        },
                        Output = []
                    };

                    yield return new ResponseFailed
                    {
                        SequenceNumber = sequence,
                        Response = failedResult
                    };
                    yield break;
            }
        }

        var fallbackAnswer = new LLMLayerAnswerResponse
        {
            Answer = JsonSerializer.SerializeToElement(output.ToString(), JsonWeb),
            Sources = sources,
            Images = images,
            ResponseTime = responseTime,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelCost = modelCost,
            LlmlayerCost = llmlayerCost
        };

        var fallbackResult = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = created,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = "completed",
            Model = options.Model ?? string.Empty,
            Temperature = options.Temperature,
            Metadata = BuildResponseMetadata(options.Metadata, fallbackAnswer),
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls,
            Usage = BuildUsage(fallbackAnswer),
            Output =
            [
                new
                {
                    id = itemId,
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = output.ToString()
                        }
                    }
                }
            ]
        };

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = output.ToString()
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = fallbackResult
        };
    }
}

