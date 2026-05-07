using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private const string BodyBuilderModelId = "openrouter/bodybuilder";
    private const string BodyBuilderToolName = "openrouter_bodybuilder_execute";

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsBodyBuilderModel(chatRequest.Model))
        {
            await foreach (var uiPart in StreamBodyBuilderAsync(chatRequest, cancellationToken))
                yield return uiPart;

            yield break;
        }

        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }

        yield break;
    }

    private async IAsyncEnumerable<UIMessagePart> StreamBodyBuilderAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        var generatedJson = new StringBuilder();
        var pendingFinishParts = new List<FinishUIPart>();

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                switch (uiPart)
                {
                    case TextDeltaUIMessageStreamPart textDelta:
                        generatedJson.Append(textDelta.Delta);
                        break;

                    case FinishUIPart finish:
                        pendingFinishParts.Add(finish);
                        break;

                    case not TextStartUIMessageStreamPart and not TextEndUIMessageStreamPart:
                        yield return uiPart;
                        break;
                }
            }
        }

        var toolCallId = $"or_bodybuilder_{Guid.NewGuid():N}";

        if (!TryParseBodyBuilderRequests(generatedJson.ToString(), out var generatedRequests, out var parseError))
        {
            yield return CreateBodyBuilderToolOutputError(toolCallId, parseError);
            yield return CreateBodyBuilderFinishPart(chatRequest.Model, pendingFinishParts);
            yield break;
        }

        yield return CreateBodyBuilderToolInput(toolCallId, generatedRequests);

        var generatedResults = new List<BodyBuilderGeneratedRequestResult>();
        await foreach (var generatedPart in StreamGeneratedRequestsAsync(
            generatedRequests,
            toolCallId,
            generatedResults,
            cancellationToken))
        {
            yield return generatedPart;
        }

        yield return CreateBodyBuilderToolOutput(toolCallId, generatedResults);
        yield return CreateBodyBuilderFinishPart(chatRequest.Model, pendingFinishParts, generatedResults);
    }

    private async IAsyncEnumerable<UIMessagePart> StreamGeneratedRequestsAsync(
        IReadOnlyList<BodyBuilderGeneratedRequest> generatedRequests,
        string toolCallId,
        List<BodyBuilderGeneratedRequestResult> results,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var streamedParts = new ConcurrentQueue<IndexedUIMessagePart>();
        var resultTasks = generatedRequests
            .Select((request, index) => ExecuteGeneratedRequestAsync(
                request,
                index,
                toolCallId,
                streamedParts,
                cancellationToken))
            .ToArray();

        var emittedCount = 0;
        while (!Task.WhenAll(resultTasks).IsCompleted || emittedCount < streamedParts.Count)
        {
            while (streamedParts.TryDequeue(out var streamedPart))
            {
                emittedCount++;
                yield return streamedPart.Part;
            }

            if (!Task.WhenAll(resultTasks).IsCompleted)
                await Task.Delay(10, cancellationToken);
        }

        while (streamedParts.TryDequeue(out var streamedPart))
            yield return streamedPart.Part;

        results.AddRange(await Task.WhenAll(resultTasks));
    }

    private async Task<BodyBuilderGeneratedRequestResult> ExecuteGeneratedRequestAsync(
        BodyBuilderGeneratedRequest generatedRequest,
        int index,
        string toolCallId,
        ConcurrentQueue<IndexedUIMessagePart> streamedParts,
        CancellationToken cancellationToken)
    {
        var emittedTextPrefix = false;
        var activeTextId = $"bodybuilder-generated-{index}";
        var activeTextStarted = false;
        var activeTextEnded = false;
        var model = generatedRequest.Model;
        FinishUIPart? finishPart = null;
        int? inputTokens = null;
        int? outputTokens = null;
        int? totalTokens = null;
        decimal? gatewayCost = null;

        try
        {
            var parsedUnified = generatedRequest.Request.ToUnifiedRequest(GetIdentifier());
            var unified = new AIRequest
            {
                ProviderId = parsedUnified.ProviderId,
                Model = parsedUnified.Model,
                Id = parsedUnified.Id,
                Instructions = parsedUnified.Instructions,
                Input = parsedUnified.Input,
                Temperature = parsedUnified.Temperature,
                TopP = parsedUnified.TopP,
                MaxOutputTokens = parsedUnified.MaxOutputTokens,
                MaxToolCalls = parsedUnified.MaxToolCalls,
                Stream = true,
                ParallelToolCalls = parsedUnified.ParallelToolCalls,
                ToolChoice = parsedUnified.ToolChoice,
                ResponseFormat = parsedUnified.ResponseFormat,
                Tools = parsedUnified.Tools,
                Metadata = MergeGeneratedRequestMetadata(generatedRequest, index, toolCallId)
            };

            await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(unified, cancellationToken: cancellationToken))
            {
                foreach (var uiPart in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                {
                    if (uiPart is FinishUIPart finish)
                    {
                        finishPart = finish;
                        inputTokens = SumNullable(inputTokens, finish.MessageMetadata?.Usage.PromptTokens);
                        outputTokens = SumNullable(outputTokens, finish.MessageMetadata?.Usage.CompletionTokens);
                        totalTokens = SumNullable(totalTokens, finish.MessageMetadata?.Usage.TotalTokens);
                        gatewayCost = SumNullable(gatewayCost, finish.MessageMetadata?.Gateway?.Cost);
                        continue;
                    }

                    if (uiPart is TextStartUIMessageStreamPart textStart)
                    {
                        activeTextId = textStart.Id;
                        activeTextStarted = true;
                        activeTextEnded = false;
                        EnqueueStreamPart(streamedParts, AddGeneratedRequestMetadata(uiPart, generatedRequest, index, toolCallId));
                        continue;
                    }

                    if (uiPart is TextDeltaUIMessageStreamPart textDelta && !emittedTextPrefix)
                    {
                        emittedTextPrefix = true;
                        activeTextId = textDelta.Id;

                        if (!activeTextStarted)
                        {
                            activeTextStarted = true;
                            EnqueueStreamPart(streamedParts, new TextStartUIMessageStreamPart
                            {
                                Id = activeTextId,
                                ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
                            });
                        }

                        EnqueueStreamPart(streamedParts, new TextDeltaUIMessageStreamPart
                        {
                            Id = activeTextId,
                            Delta = $"\n\n[{model}]\n",
                            ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
                        });
                    }

                    if (uiPart is TextEndUIMessageStreamPart textEnd)
                    {
                        activeTextId = textEnd.Id;
                        activeTextEnded = true;
                        EnqueueStreamPart(streamedParts, AddGeneratedRequestMetadata(uiPart, generatedRequest, index, toolCallId));
                        continue;
                    }

                    EnqueueStreamPart(streamedParts, AddGeneratedRequestMetadata(uiPart, generatedRequest, index, toolCallId));
                }
            }

            if (activeTextStarted && !activeTextEnded)
            {
                EnqueueStreamPart(streamedParts, new TextEndUIMessageStreamPart
                {
                    Id = activeTextId,
                    ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
                });
            }

            return new BodyBuilderGeneratedRequestResult(index, model, "completed", null, finishPart?.MessageMetadata, inputTokens, outputTokens, totalTokens, gatewayCost);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!activeTextStarted)
            {
                activeTextStarted = true;
                EnqueueStreamPart(streamedParts, new TextStartUIMessageStreamPart
                {
                    Id = activeTextId,
                    ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
                });
            }

            EnqueueStreamPart(streamedParts, new TextDeltaUIMessageStreamPart
            {
                Id = activeTextId,
                Delta = $"\n\n[{model}]\nError: {ex.Message}\n",
                ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
            });

            if (!activeTextEnded)
            {
                EnqueueStreamPart(streamedParts, new TextEndUIMessageStreamPart
                {
                    Id = activeTextId,
                    ProviderMetadata = CreateGeneratedRequestTextProviderMetadata(generatedRequest, index, toolCallId)
                });
            }

            return new BodyBuilderGeneratedRequestResult(index, model, "failed", ex.Message, finishPart?.MessageMetadata, inputTokens, outputTokens, totalTokens, gatewayCost);
        }
    }

    private static int? SumNullable(int? left, int? right)
        => left is null && right is null ? null : (left ?? 0) + (right ?? 0);

    private static decimal? SumNullable(decimal? left, decimal? right)
        => left is null && right is null ? null : (left ?? 0m) + (right ?? 0m);

    private static void EnqueueStreamPart(ConcurrentQueue<IndexedUIMessagePart> parts, UIMessagePart part)
        => parts.Enqueue(new IndexedUIMessagePart(Interlocked.Increment(ref _bodyBuilderStreamSequence), part));

    private static int _bodyBuilderStreamSequence;

    private static bool IsBodyBuilderModel(string? model)
        => string.Equals(model?.Trim(), BodyBuilderModelId, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBodyBuilderRequests(
        string generatedJson,
        out List<BodyBuilderGeneratedRequest> generatedRequests,
        out string error)
    {
        generatedRequests = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(generatedJson))
        {
            error = "Body Builder returned an empty request body.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(generatedJson);
            if (!doc.RootElement.TryGetProperty("requests", out var requestsElement)
                || requestsElement.ValueKind != JsonValueKind.Array)
            {
                error = "Body Builder response did not contain a requests array.";
                return false;
            }

            var index = 0;
            foreach (var requestElement in requestsElement.EnumerateArray())
            {
                if (requestElement.ValueKind != JsonValueKind.Object)
                    continue;

                var request = requestElement.Clone();
                var model = request.TryGetProperty("model", out var modelElement)
                            && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;

                generatedRequests.Add(new BodyBuilderGeneratedRequest(
                    index++,
                    string.IsNullOrWhiteSpace(model) ? "unknown" : model!,
                    request));
            }

            if (generatedRequests.Count > 0)
                return true;

            error = "Body Builder did not generate any executable request objects.";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Body Builder response JSON could not be parsed: {ex.Message}";
            return false;
        }
    }

    private static Dictionary<string, object?> MergeGeneratedRequestMetadata(
        BodyBuilderGeneratedRequest request,
        int index,
        string toolCallId)
    {
        var metadata = request.Request.ToUnifiedRequest("openrouter").Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? [];
        metadata["openrouter.bodybuilder.tool_call_id"] = toolCallId;
        metadata["openrouter.bodybuilder.request_index"] = index;
        metadata["openrouter.bodybuilder.model"] = request.Model;
        return metadata;
    }

    private static ToolCallPart CreateBodyBuilderToolInput(
        string toolCallId,
        IReadOnlyList<BodyBuilderGeneratedRequest> generatedRequests)
        => new()
        {
            ToolCallId = toolCallId,
            ToolName = BodyBuilderToolName,
            Title = "Execute OpenRouter Body Builder requests",
            ProviderExecuted = true,
            Input = CreateCompactBodyBuilderToolInput(generatedRequests),
            ProviderMetadata = CreateBodyBuilderProviderMetadata(toolCallId)
        };

    private static object CreateCompactBodyBuilderToolInput(IReadOnlyList<BodyBuilderGeneratedRequest> generatedRequests)
    {
        var messages = generatedRequests
            .Select(request => request.Request.TryGetProperty("messages", out var messagesElement)
                ? messagesElement.Clone()
                : default)
            .FirstOrDefault(messagesElement => messagesElement.ValueKind != JsonValueKind.Undefined);

        var input = new Dictionary<string, object?>
        {
            ["models"] = generatedRequests.Select(request => request.Model).ToArray(),
        };

        if (messages.ValueKind != JsonValueKind.Undefined)
            input["messages"] = messages;

        return input;
    }

    private static ToolOutputAvailablePart CreateBodyBuilderToolOutput(
        string toolCallId,
        IReadOnlyList<BodyBuilderGeneratedRequestResult> results)
        => new()
        {
            ToolCallId = toolCallId,
            ProviderExecuted = true,
            Output = new
            {
                results = results.Select(result => new
                {
                    index = result.Index,
                    model = result.Model,
                    status = result.Status,
                    error = result.Error
                }).ToArray()
            },
            ProviderMetadata = CreateBodyBuilderProviderMetadata(toolCallId)
        };

    private static ToolOutputErrorPart CreateBodyBuilderToolOutputError(string toolCallId, string error)
        => new()
        {
            ToolCallId = toolCallId,
            ErrorText = error,
            ProviderExecuted = true,
            ProviderMetadata = CreateBodyBuilderProviderMetadata(toolCallId)
        };

    private static FinishUIPart CreateBodyBuilderFinishPart(
        string? requestedModel,
        IReadOnlyList<FinishUIPart> pendingFinishParts,
        IReadOnlyList<BodyBuilderGeneratedRequestResult>? generatedResults = null)
    {
        var original = pendingFinishParts.LastOrDefault();
        var aggregated = AggregateGeneratedFinishMetadata(original?.MessageMetadata, generatedResults);
        return new FinishUIPart
        {
            FinishReason = original?.FinishReason ?? "stop",
            MessageMetadata = aggregated ?? original?.MessageMetadata ?? FinishMessageMetadata.Create(
                requestedModel ?? BodyBuilderModelId,
                DateTimeOffset.UtcNow)
        };
    }

    private static FinishMessageMetadata? AggregateGeneratedFinishMetadata(
        FinishMessageMetadata? bodyBuilderMetadata,
        IReadOnlyList<BodyBuilderGeneratedRequestResult>? generatedResults)
    {
        if (generatedResults is not { Count: > 0 })
            return null;

        var promptTokens = SumNullable(bodyBuilderMetadata?.Usage.PromptTokens, SumGeneratedInt(generatedResults, result => result.InputTokens));
        var completionTokens = SumNullable(bodyBuilderMetadata?.Usage.CompletionTokens, SumGeneratedInt(generatedResults, result => result.OutputTokens));
        var totalTokens = SumNullable(bodyBuilderMetadata?.Usage.TotalTokens, SumGeneratedInt(generatedResults, result => result.TotalTokens));
        var cost = SumNullable(bodyBuilderMetadata?.Gateway?.Cost, SumGeneratedDecimal(generatedResults, result => result.GatewayCost));

        return FinishMessageMetadata.Create(
            bodyBuilderMetadata?.Model ?? BodyBuilderModelId,
            bodyBuilderMetadata?.Timestamp ?? DateTimeOffset.UtcNow,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: totalTokens,
            temperature: bodyBuilderMetadata?.Temperature,
            gateway: cost is > 0 ? new FinishGatewayMetadata { Cost = cost } : bodyBuilderMetadata?.Gateway,
            additionalProperties: new Dictionary<string, object?>
            {
                ["bodybuilderGeneratedModels"] = generatedResults.Select(item => item.Model).ToArray(),
                ["bodybuilderModel"] = bodyBuilderMetadata?.Model
            });
    }

    private static int? SumGeneratedInt(
        IReadOnlyList<BodyBuilderGeneratedRequestResult> generatedResults,
        Func<BodyBuilderGeneratedRequestResult, int?> selector)
    {
        int? sum = null;
        foreach (var result in generatedResults)
            sum = SumNullable(sum, selector(result));

        return sum;
    }

    private static decimal? SumGeneratedDecimal(
        IReadOnlyList<BodyBuilderGeneratedRequestResult> generatedResults,
        Func<BodyBuilderGeneratedRequestResult, decimal?> selector)
    {
        decimal? sum = null;
        foreach (var result in generatedResults)
            sum = SumNullable(sum, selector(result));

        return sum;
    }

    private static UIMessagePart AddGeneratedRequestMetadata(
        UIMessagePart uiPart,
        BodyBuilderGeneratedRequest request,
        int index,
        string toolCallId)
    {
        var metadata = CreateGeneratedRequestTextProviderMetadata(request, index, toolCallId);
        return uiPart switch
        {
            TextStartUIMessageStreamPart part => new TextStartUIMessageStreamPart
            {
                Id = part.Id,
                ProviderMetadata = metadata
            },
            TextDeltaUIMessageStreamPart part => new TextDeltaUIMessageStreamPart
            {
                Id = part.Id,
                Delta = part.Delta,
                ProviderMetadata = metadata
            },
            TextEndUIMessageStreamPart part => new TextEndUIMessageStreamPart
            {
                Id = part.Id,
                ProviderMetadata = metadata
            },
            _ => uiPart
        };
    }

    private static Dictionary<string, object> CreateGeneratedRequestTextProviderMetadata(
        BodyBuilderGeneratedRequest request,
        int index,
        string toolCallId)
        => new()
        {
            [GetIdentifierStatic()] = new Dictionary<string, object>
            {
                ["bodybuilder_tool_call_id"] = toolCallId,
                ["bodybuilder_request_index"] = index,
                ["model"] = request.Model,
                ["type"] = "bodybuilder_generated_request"
            }
        };

    private static Dictionary<string, Dictionary<string, object>?> CreateBodyBuilderProviderMetadata(string toolCallId)
        => new()
        {
            [GetIdentifierStatic()] = new Dictionary<string, object>
            {
                ["tool_use_id"] = toolCallId,
                ["tool_name"] = BodyBuilderToolName,
                ["type"] = "bodybuilder_generated_requests"
            }
        };

    private static string GetIdentifierStatic() => nameof(OpenRouter).ToLowerInvariant();

    private sealed record BodyBuilderGeneratedRequest(int Index, string Model, JsonElement Request);

    private sealed record BodyBuilderGeneratedRequestResult(
        int Index,
        string Model,
        string Status,
        string? Error,
        FinishMessageMetadata? MessageMetadata,
        int? InputTokens,
        int? OutputTokens,
        int? TotalTokens,
        decimal? GatewayCost);

    private sealed record IndexedUIMessagePart(int Sequence, UIMessagePart Part);
}
