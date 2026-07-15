using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.Cortecs;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Cortecs;

public class CortecsProviderCostingTests
{
    private const decimal RawCortecsSampleCost = 902.0m;
    private const decimal ExpectedGatewaySampleCost = 0.0009020m;

    [Fact]
    public void ChatCompletion_enrichment_scales_cortecs_usage_cost_to_gateway_metadata_and_preserves_raw_usage_cost()
    {
        var response = new ChatCompletion
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = CortecsSampleUsage()
        };

        CortecsProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(RawCortecsSampleCost, usage.GetProperty("cost").GetDecimal());

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_scales_string_cortecs_usage_cost_to_gateway_metadata()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = UsageElement("""
            {
                "completion_tokens": 29,
                "prompt_tokens": 503,
                "total_tokens": 532,
                "cost": "902.0"
            }
            """)
        };

        CortecsProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Streaming_usage_tail_gets_scaled_gateway_cost_for_unified_finish_and_api_chat_finish_metadata()
    {
        var finishUpdate = new ChatCompletionUpdate
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Choices =
            [
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = "stop"
                }
            ]
        };

        var usageUpdate = new ChatCompletionUpdate
        {
            Id = "5o1XatYe5sbs0w-x74G5CA",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = CortecsSampleUsage()
        };

        string? lastFinishReason = null;
        CortecsProvider.NormalizeStreamingUpdateForGatewayCostForTests(finishUpdate, ref lastFinishReason);
        CortecsProvider.NormalizeStreamingUpdateForGatewayCostForTests(usageUpdate, ref lastFinishReason);
        CortecsProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(usageUpdate);

        var gateway = usageUpdate.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(ExpectedGatewaySampleCost, gateway?.GetProperty("cost").GetDecimal());

        var rawUsage = Assert.IsType<JsonElement>(usageUpdate.Usage);
        Assert.Equal(RawCortecsSampleCost, rawUsage.GetProperty("cost").GetDecimal());

        var finishEvent = usageUpdate
            .ToUnifiedStreamEvents("cortecs")
            .Single(streamEvent => streamEvent.Event.Type == "finish");

        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(503, finishData.InputTokens);
        Assert.Equal(29, finishData.OutputTokens);
        Assert.Equal(532, finishData.TotalTokens);
        Assert.Equal(ExpectedGatewaySampleCost, finishData.MessageMetadata?.Gateway?.Cost);

        var finishPart = Assert.IsType<FinishUIPart>(
            VercelUnifiedMapper.ToUIMessagePart(finishEvent.Event, "cortecs").Single());

        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(503, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(29, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(532, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.Equal(ExpectedGatewaySampleCost, finishPart.MessageMetadata?.Gateway?.Cost);

        Assert.NotNull(finishPart.MessageMetadata?.ProviderMetadata);
        var providerMetadata = finishPart.MessageMetadata.ProviderMetadata;
        Assert.True(providerMetadata.TryGetValue("gateway", out var gatewayMetadata));
        Assert.Equal(ExpectedGatewaySampleCost, gatewayMetadata.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletion_enrichment_without_cortecs_usage_cost_is_noop()
    {
        var response = new ChatCompletion
        {
            Id = "5o1XatYe5sbs0w-no-cost",
            Created = 1784122854,
            Model = "gemini-3.5-flash",
            Usage = UsageElement("""
            {
                "completion_tokens": 29,
                "prompt_tokens": 503,
                "total_tokens": 532
            }
            """)
        };

        CortecsProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        Assert.Null(response.AdditionalProperties);
    }

    [Fact]
    public async Task Synthetic_api_chat_finish_metadata_scales_raw_cortecs_usage_cost()
    {
        var provider = new RawCortecsStreamProvider(
            new ChatCompletionUpdate
            {
                Id = "yD4etAZzBCsMFyOQ",
                Created = 1784122854,
                Model = "cortecs/gemini-3.5-flash",
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    }
                ]
            },
            new ChatCompletionUpdate
            {
                Id = "yD4etAZzBCsMFyOQ",
                Created = 1784122854,
                Model = "cortecs/gemini-3.5-flash",
                Usage = UsageElement("""
                {
                    "completion_tokens": 55,
                    "prompt_tokens": 554,
                    "total_tokens": 609,
                    "cost": 1178.0
                }
                """)
            });

        FinishUIPart? finishPart = null;
        await foreach (var streamEvent in provider.StreamUnifiedViaChatCompletionsAsync(new AIRequest
        {
            ProviderId = "cortecs",
            Model = "cortecs/gemini-3.5-flash"
        }))
        {
            if (streamEvent.Event.Type != "finish")
                continue;

            finishPart = Assert.IsType<FinishUIPart>(
                VercelUnifiedMapper.ToUIMessagePart(streamEvent.Event, "cortecs").Single());
        }

        Assert.NotNull(finishPart);
        Assert.Equal(0.001178m, finishPart.MessageMetadata?.Gateway?.Cost);

        Assert.NotNull(finishPart.MessageMetadata?.ProviderMetadata);
        Assert.True(finishPart.MessageMetadata.ProviderMetadata.TryGetValue("gateway", out var gatewayMetadata));
        Assert.Equal(0.001178m, gatewayMetadata.GetProperty("cost").GetDecimal());
    }

    private static JsonElement CortecsSampleUsage()
        => UsageElement("""
        {
            "completion_tokens": 29,
            "prompt_tokens": 503,
            "total_tokens": 532,
            "completion_tokens_details": {
                "text_tokens": 29
            },
            "prompt_tokens_details": {
                "text_tokens": 503
            },
            "cost": 902.0,
            "cost_details": {
                "prompt_cost": 670,
                "cache_read_cost": 0,
                "cache_write_cost": 0,
                "completion_cost": 232,
                "prompt_audio_cost": 0
            }
        }
        """);

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RawCortecsStreamProvider(params ChatCompletionUpdate[] updates) : IModelProvider
    {
        public string GetIdentifier() => "cortecs";

        public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

