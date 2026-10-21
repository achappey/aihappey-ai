using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesUsageAccountingTests
{
    [Theory]
    [InlineData(null, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(700, 700)]
    [InlineData(0, 0)]
    public void Native_messages_telemetry_uses_cache_inclusive_counts(int? cacheCreation, int expectedWrite)
    {
        // Native Messages telemetry uses this same helper for both response modes.
        var native = CreateUsage(cacheCreation);
        var normalized = Assert.IsType<AIUsage>(native.ToUnifiedUsage());
        Assert.Equal(10100 + expectedWrite, normalized.InputTokens);
        Assert.Equal(10150 + expectedWrite, normalized.TotalTokens);
        Assert.Equal(50, normalized.TotalTokens - normalized.InputTokens);
        Assert.Equal(100, native.InputTokens);
        Assert.Equal(cacheCreation, native.CacheCreationInputTokens);
    }

    [Fact]
    public void Missing_and_uncached_usage_keep_their_meaning()
    {
        Assert.Null(((MessagesUsage?)null).ToUnifiedUsage());
        var empty = Assert.IsType<AIUsage>(new MessagesUsage().ToUnifiedUsage());
        Assert.Null(empty.InputTokens);
        Assert.Null(empty.OutputTokens);
        Assert.Null(empty.TotalTokens);

        var uncached = Assert.IsType<AIUsage>(new MessagesUsage
        {
            InputTokens = 100, OutputTokens = 50
        }.ToUnifiedUsage());
        Assert.Equal(100, uncached.InputTokens);
        Assert.Equal(50, uncached.OutputTokens);
        Assert.Equal(150, uncached.TotalTokens);
        Assert.Null(uncached.CachedInputTokens);
        Assert.Null(uncached.CacheWriteInputTokens);
    }

    [Theory]
    [InlineData(null, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(700, 700)]
    [InlineData(0, 0)]
    public void Streaming_counts_cached_input_as_input_not_output(int? cacheCreation, int expectedWrite)
    {
        var rawUsage = CreateUsage(cacheCreation);
        var state = new MessagesUnifiedMapper.MessagesStreamMappingState();
        var start = new MessageStreamPart
        {
            Type = "message_start",
            Message = new MessagesResponse
            {
                Id = "msg_cached", Model = "claude-sonnet-4-6", Role = "assistant",
                Usage = rawUsage
            }
        };
        _ = start.ToUnifiedStreamEvents("anthropic", state).ToList();

        // Anthropic usage updates are cumulative snapshots, including repeated counts.
        foreach (var output in new[] { 5, 25, 50, 50 })
        {
            _ = new MessageStreamPart
            {
                Type = "message_delta",
                Usage = new MessagesUsage { OutputTokens = output }
            }.ToUnifiedStreamEvents("anthropic", state).ToList();
        }

        var finishEvent = Assert.Single(new MessageStreamPart { Type = "message_stop" }
            .ToUnifiedStreamEvents("anthropic", state));
        var finish = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);
        Assert.Equal(10100 + expectedWrite, finish.InputTokens);
        Assert.Equal(50, finish.OutputTokens);
        Assert.Equal(10150 + expectedWrite, finish.TotalTokens);

        // These are the exact fields captured by optional chat telemetry.
        var ui = Assert.IsType<FinishUIPart>(Assert.Single(finishEvent.Event.ToUIMessagePart("anthropic")));
        var usage = Assert.IsType<Usage>(ui.MessageMetadata?.Usage);
        Assert.Equal(10100 + expectedWrite, usage.PromptTokens);
        Assert.Equal(50, usage.CompletionTokens);
        Assert.Equal(50, usage.TotalTokens - usage.PromptTokens);

        var providerMetadata = JsonSerializer.SerializeToElement(ui.MessageMetadata!.ProviderMetadata);
        var preservedUsage = providerMetadata.GetProperty("anthropic").GetProperty("usage");
        Assert.Equal(100, preservedUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(10000, preservedUsage.GetProperty("cache_read_input_tokens").GetInt32());
    }

    [Fact]
    public void Non_streaming_normalizes_usage_and_preserves_native_round_trip()
    {
        var rawUsage = CreateUsage(1000);
        var original = new MessagesResponse
        {
            Id = "msg_cached", Model = "claude-sonnet-4-6", Role = "assistant",
            Usage = rawUsage
        };

        var unified = original.ToUnifiedResponse("anthropic");
        var usage = Assert.IsType<AIUsage>(unified.Usage);
        Assert.Equal(11100, usage.InputTokens);
        Assert.Equal(50, usage.OutputTokens);
        Assert.Equal(11150, usage.TotalTokens);
        Assert.Equal(10000, usage.CachedInputTokens);
        Assert.Equal(1000, usage.CacheWriteInputTokens);

        var responseUsage = JsonSerializer.SerializeToElement(unified.ToResponseResult().Usage);
        Assert.Equal(11100, responseUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(50, responseUsage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(11150, responseUsage.GetProperty("total_tokens").GetInt32());

        var roundTrip = unified.ToMessagesResponse();
        Assert.Equal(100, roundTrip.Usage?.InputTokens);
        Assert.Equal(10000, roundTrip.Usage?.CacheReadInputTokens);
        Assert.Equal(1000, roundTrip.Usage?.CacheCreationInputTokens);
        Assert.Equal(400, roundTrip.Usage?.CacheCreation?.Ephemeral1hInputTokens);
        Assert.Equal(50, roundTrip.Usage?.OutputTokens);
        Assert.Equal(100, rawUsage.InputTokens);
    }

    [Fact]
    public void Normalized_usage_without_raw_metadata_converts_back_to_native_exclusive_input()
    {
        var unified = new AIResponse
        {
            ProviderId = "openai", Model = "test", Status = "completed",
            Usage = new AIUsage
            {
                InputTokens = 11100, OutputTokens = 50, TotalTokens = 11150,
                CachedInputTokens = 10000, CacheWriteInputTokens = 1000
            }
        };

        var native = unified.ToMessagesResponse();
        Assert.Equal(100, native.Usage?.InputTokens);
        Assert.Equal(10000, native.Usage?.CacheReadInputTokens);
        Assert.Equal(1000, native.Usage?.CacheCreationInputTokens);
    }

    private static MessagesUsage CreateUsage(int? cacheCreation) => new()
    {
        InputTokens = 100,
        OutputTokens = 50,
        CacheReadInputTokens = 10000,
        CacheCreationInputTokens = cacheCreation,
        CacheCreation = new MessagesCacheCreation
        {
            Ephemeral1hInputTokens = 400,
            Ephemeral5mInputTokens = 600
        }
    };
}
