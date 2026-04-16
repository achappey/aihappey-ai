using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesStreamFixtureTests
{
    private const string TypedFixturePath = "Fixtures/messages/typed/basic-messages-stream.json";
    private const string RawFixturePath = "Fixtures/messages/raw/basic-messages-stream.jsonl";
    private const string ReasoningRawFixturePath = "Fixtures/messages/raw/reasoning-messages-stream.jsonl";
    private const string ReasoningAndProviderToolCallsRawFixturePath = "Fixtures/messages/raw/reasoning-and-provider-tool-calls-stream.jsonl";
    private const string ProviderId = "fixture-provider";
    private const string Model = "claude-haiku-4-5-20251001";
    private const string MessageId = "msg_017Kux9bNH5F1gph8C2FZhP1";

    [Fact]
    public void Typed_messages_fixture_deserializes_expected_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);

        Assert.Equal(
            [
                "message_start",
                "content_block_start",
                "ping",
                "content_block_delta",
                "content_block_delta",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            ],
            parts.Select(part => part.Type).ToList());

        var messageStart = parts[0];
        Assert.Equal(MessageId, messageStart.Message?.Id);
        Assert.Equal(Model, messageStart.Message?.Model);
        Assert.Equal("assistant", messageStart.Message?.Role);
        Assert.Equal(12, messageStart.Message?.Usage?.InputTokens);
        Assert.Equal(1, messageStart.Message?.Usage?.OutputTokens);

        var contentBlockStart = parts[1];
        Assert.Equal(0, contentBlockStart.Index);
        Assert.Equal("text", contentBlockStart.ContentBlock?.Type);
        Assert.Equal(string.Empty, contentBlockStart.ContentBlock?.Text);

        var firstDelta = parts[3];
        Assert.Equal(0, firstDelta.Index);
        Assert.Equal("text_delta", firstDelta.Delta?.Type);
        Assert.Equal("#", firstDelta.Delta?.Text);

        var secondDelta = parts[4];
        Assert.Equal(" yow\n\nHey! 👋 What's up? How can", secondDelta.Delta?.Text);

        var thirdDelta = parts[5];
        Assert.Equal(" I help you?", thirdDelta.Delta?.Text);

        var messageDelta = parts[7];
        Assert.Equal("end_turn", messageDelta.Delta?.StopReason);
        Assert.Equal(12, messageDelta.Usage?.InputTokens);
        Assert.Equal(23, messageDelta.Usage?.OutputTokens);

        var messageStop = parts[8];
        Assert.Null(messageStop.Metadata);
    }

    [Fact]
    public void Raw_messages_fixture_deserializes_expected_stream_parts()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(RawFixturePath);

        Assert.Equal(
            [
                "message_start",
                "content_block_start",
                "ping",
                "content_block_delta",
                "content_block_delta",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            ],
            parts.Select(part => part.Type).ToList());

        var messageStart = parts[0];
        Assert.Equal("msg_01FeYqRcJzc1X3ngnRZ7AKRg", messageStart.Message?.Id);
        Assert.Equal("claude-opus-4-6", messageStart.Message?.Model);
        Assert.Equal("assistant", messageStart.Message?.Role);
        Assert.Equal(10, messageStart.Message?.Usage?.InputTokens);
        Assert.Equal(3, messageStart.Message?.Usage?.OutputTokens);

        Assert.Equal("Hello! ", parts[3].Delta?.Text);
        Assert.Equal("👋 Welcome! How can I help you today? Whether you want to chat, ask questions, or work through", parts[4].Delta?.Text);
        Assert.Equal(" something together, I'm here for you. 😊", parts[5].Delta?.Text);

        var messageDelta = parts[7];
        Assert.Equal("end_turn", messageDelta.Delta?.StopReason);
        Assert.Equal(10, messageDelta.Usage?.InputTokens);
        Assert.Equal(42, messageDelta.Usage?.OutputTokens);
    }

    [Fact]
    public void Typed_and_raw_messages_fixtures_produce_the_same_stream_part_types()
    {
        var typed = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var raw = FixtureFileLoader.LoadMessageRawFixture(RawFixturePath);

        Assert.Equal(typed.Select(part => part.Type), raw.Select(part => part.Type));
    }

    [Fact]
    public void Typed_messages_fixture_maps_to_expected_unified_events_and_finish_payload()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var state = new MessagesUnifiedMapper.MessagesStreamMappingState();

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, state))
            .ToList();

        Assert.Equal(
            [
                "text-start",
                "text-delta",
                "text-delta",
                "text-delta",
                "text-end",
                "finish"
            ],
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList());

        var textDeltas = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(
            [
                "#",
                " yow\n\nHey! 👋 What's up? How can",
                " I help you?"
            ],
            textDeltas);
        Assert.Equal("# yow\n\nHey! 👋 What's up? How can I help you?", string.Concat(textDeltas));

        var finishEvent = unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);

        Assert.Equal(Model, finishData.Model);
        Assert.Equal("stop", finishData.FinishReason);
        Assert.Equal(12, finishData.InputTokens);
        Assert.Equal(23, finishData.OutputTokens);
        Assert.Equal(35, finishData.TotalTokens);
        Assert.Null(finishData.StopSequence);
        Assert.NotNull(finishData.MessageMetadata);
        Assert.Equal(Model, finishData.MessageMetadata?.Model);
        Assert.Equal(12, finishData.MessageMetadata?.Usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(23, finishData.MessageMetadata?.Usage.GetProperty("output_tokens").GetInt32());
        Assert.IsType<long>(finishData.CompletedAt);

        Assert.Equal("message_stop", finishEvent.Metadata?["messages.stream.type"]);
    }

    [Fact]
    public void Typed_messages_fixture_maps_to_expected_vercel_ui_stream_parts_and_finish_metadata()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();

        var uiParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .Where(streamEvent => streamEvent.Event.Type is "text-start" or "text-delta" or "text-end" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        Assert.Equal(
            [
                "text-start",
                "text-delta",
                "text-delta",
                "text-delta",
                "text-end",
                "finish"
            ],
            uiParts.Select(part => part.Type).ToList());

        var textDeltas = uiParts
            .OfType<TextDeltaUIMessageStreamPart>()
            .Select(part => part.Delta)
            .ToList();

        Assert.Equal(
            [
                "#",
                " yow\n\nHey! 👋 What's up? How can",
                " I help you?"
            ],
            textDeltas);
        Assert.Equal("# yow\n\nHey! 👋 What's up? How can I help you?", string.Concat(textDeltas));

        var finishPart = Assert.IsType<FinishUIPart>(uiParts[^1]);
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal($"{ProviderId}/{Model}", finishPart.MessageMetadata?.Model);
        Assert.Equal(12, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(23, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(35, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.True(finishPart.MessageMetadata?.Timestamp > DateTimeOffset.UnixEpoch);

        var providerMetadata = Assert.Contains(ProviderId, finishPart.MessageMetadata?.AdditionalProperties ?? new Dictionary<string, JsonElement>());
        var providerUsage = providerMetadata.GetProperty("usage");
        Assert.Equal(12, providerUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(23, providerUsage.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void Raw_messages_fixture_maps_to_expected_vercel_ui_stream_parts_and_finish_metadata()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(RawFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();

        var uiParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .Where(streamEvent => streamEvent.Event.Type is "text-start" or "text-delta" or "text-end" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        Assert.Equal(
            [
                "text-start",
                "text-delta",
                "text-delta",
                "text-delta",
                "text-end",
                "finish"
            ],
            uiParts.Select(part => part.Type).ToList());

        var textDeltas = uiParts
            .OfType<TextDeltaUIMessageStreamPart>()
            .Select(part => part.Delta)
            .ToList();

        Assert.Equal(
            [
                "Hello! ",
                "👋 Welcome! How can I help you today? Whether you want to chat, ask questions, or work through",
                " something together, I'm here for you. 😊"
            ],
            textDeltas);
        Assert.Equal(
            "Hello! 👋 Welcome! How can I help you today? Whether you want to chat, ask questions, or work through something together, I'm here for you. 😊",
            string.Concat(textDeltas));

        var finishPart = Assert.IsType<FinishUIPart>(uiParts[^1]);
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal($"{ProviderId}/claude-opus-4-6", finishPart.MessageMetadata?.Model);
        Assert.Equal(10, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(42, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(52, finishPart.MessageMetadata?.Usage.TotalTokens);
        Assert.True(finishPart.MessageMetadata?.Timestamp > DateTimeOffset.UnixEpoch);

        var providerMetadata = Assert.Contains(ProviderId, finishPart.MessageMetadata?.AdditionalProperties ?? new Dictionary<string, JsonElement>());
        var providerUsage = providerMetadata.GetProperty("usage");
        Assert.Equal(10, providerUsage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(42, providerUsage.GetProperty("output_tokens").GetInt32());
    }

    [Fact]
    public void Raw_reasoning_messages_fixture_maps_to_unified_reasoning_events_and_ui_parts_with_provider_scoped_signature_metadata()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(ReasoningRawFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();
        var originalSignature = parts.Single(part => part.Delta?.Type == "signature_delta").Delta?.Signature;

        var unifiedEvents = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .ToList();

        FixtureAssertions.AssertContainsSubsequence(
            unifiedEvents.Select(streamEvent => streamEvent.Event.Type).ToList(),
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end",
            "text-start",
            "text-delta",
            "text-end",
            "finish");

        var reasoningStartEvent = unifiedEvents.First(streamEvent => streamEvent.Event.Type == "reasoning-start");
        var reasoningStartData = Assert.IsType<AIReasoningStartEventData>(reasoningStartEvent.Event.Data);
        Assert.Null(reasoningStartData.ProviderMetadata);

        var reasoningDeltas = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type == "reasoning-delta")
            .Select(streamEvent => Assert.IsType<AIReasoningDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(
            [
                "The user is asking me to create a",
                " poem about Groningen. This is a straightforward creative writing request. Groningen is a city in the northern Netherlands.\n\nThe user's",
                " preferred language is Dutch (nl), so I should write the poem in Dutch.\n\nLet me create",
                " an original poem about Groningen in Dutch. I'll use markdown formatting as instructed.",
                string.Empty
            ],
            reasoningDeltas);

        Assert.Equal(
            "The user is asking me to create a poem about Groningen. This is a straightforward creative writing request. Groningen is a city in the northern Netherlands.\n\nThe user's preferred language is Dutch (nl), so I should write the poem in Dutch.\n\nLet me create an original poem about Groningen in Dutch. I'll use markdown formatting as instructed.",
            string.Concat(reasoningDeltas));

        var reasoningEndEvent = unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "reasoning-end");
        var reasoningEndData = Assert.IsType<AIReasoningEndEventData>(reasoningEndEvent.Event.Data);
        var reasoningEndProviderMetadata = Assert.Contains(ProviderId, reasoningEndData.ProviderMetadata ?? new Dictionary<string, Dictionary<string, object>>());

        Assert.Equal(originalSignature, Assert.IsType<string>(reasoningEndProviderMetadata["signature"]));

        var reasoningUiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "reasoning-start" or "reasoning-delta" or "reasoning-end")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart(ProviderId))
            .ToList();

        Assert.Equal(
            [
                "reasoning-start",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-delta",
                "reasoning-end"
            ],
            reasoningUiParts.Select(part => part.Type).ToList());

        var reasoningStartPart = Assert.IsType<ReasoningStartUIPart>(reasoningUiParts[0]);
        Assert.Null(reasoningStartPart.ProviderMetadata);

        var reasoningEndPart = Assert.IsType<ReasoningEndUIPart>(reasoningUiParts[^1]);
        var uiProviderMetadata = Assert.Contains(ProviderId, reasoningEndPart.ProviderMetadata ?? new Dictionary<string, Dictionary<string, object>>());

        Assert.Equal(originalSignature, Assert.IsType<string>(uiProviderMetadata["signature"]));
    }

    [Fact]
    public void Messages_reasoning_and_provider_tool_calls_bridge_to_response_stream_parts_with_preserved_order_and_payloads()
    {
        var parts = FixtureFileLoader.LoadMessageRawFixture(ReasoningAndProviderToolCallsRawFixturePath);
        var mappingState = new MessagesUnifiedMapper.MessagesStreamMappingState();
        var originalSignature = parts.Single(part => part.Delta?.Type == "signature_delta").Delta?.Signature;
        var toolUseId = parts.Single(part => part.ContentBlock?.Type == "server_tool_use").ContentBlock?.Id;

        var responseParts = parts
            .SelectMany(part => part.ToUnifiedStreamEvents(ProviderId, mappingState))
            .Select(streamEvent => streamEvent.ToResponseStreamPart())
            .ToList();

        Assert.All(responseParts, part => Assert.IsType<ResponseUnknownEvent>(part));

        FixtureAssertions.AssertContainsSubsequence(
            responseParts.Select(part => part.Type).ToList(),
            "reasoning-start",
            "reasoning-delta",
            "reasoning-end",
            "tool-input-start",
            "tool-input-delta",
            "tool-input-available",
            "tool-output-available",
            "text-start",
            "source-url",
            "text-delta",
            "text-end",
            "finish");

        Assert.Equal(8, responseParts.Count(part => part.Type == "tool-input-delta"));
        Assert.Equal(9, responseParts.Count(part => part.Type == "source-url"));

        var reasoningEnd = Assert.IsType<ResponseUnknownEvent>(responseParts.Single(part => part.Type == "reasoning-end"));
        var reasoningProviderMetadata = reasoningEnd.Data?["providerMetadata"].GetProperty(ProviderId)
            ?? throw new InvalidOperationException("Expected provider metadata on reasoning-end response bridge event.");
        Assert.Equal(originalSignature, reasoningProviderMetadata.GetProperty("signature").GetString());

        var toolInputStart = Assert.IsType<ResponseUnknownEvent>(responseParts.Single(part => part.Type == "tool-input-start"));
        Assert.Equal("web_search", toolInputStart.Data?["toolName"].GetString());
        Assert.True(toolInputStart.Data?["providerExecuted"].GetBoolean());
        var toolInputStartProviderMetadata = toolInputStart.Data?["providerMetadata"].GetProperty(ProviderId)
            ?? throw new InvalidOperationException("Expected provider metadata on tool-input-start response bridge event.");
        Assert.Equal(JsonValueKind.Object, toolInputStartProviderMetadata.ValueKind);

        var toolInputText = string.Concat(
            responseParts
                .Where(part => part.Type == "tool-input-delta")
                .Select(part => Assert.IsType<ResponseUnknownEvent>(part).Data?["inputTextDelta"].GetString()));
        Assert.Equal("{\"query\": \"latest news war Iran 2026\"}", toolInputText);

        var toolInputAvailable = Assert.IsType<ResponseUnknownEvent>(responseParts.Single(part => part.Type == "tool-input-available"));
        Assert.Equal("latest news war Iran 2026", toolInputAvailable.Data?["input"].GetProperty("query").GetString());
        var toolInputAvailableProviderMetadata = toolInputAvailable.Data?["providerMetadata"].GetProperty(ProviderId)
            ?? throw new InvalidOperationException("Expected provider metadata on tool-input-available response bridge event.");
        Assert.Equal(JsonValueKind.Object, toolInputAvailableProviderMetadata.ValueKind);

        var toolOutputAvailable = Assert.IsType<ResponseUnknownEvent>(responseParts.Single(part => part.Type == "tool-output-available"));
        Assert.True(toolOutputAvailable.Data?["providerExecuted"].GetBoolean());
        var toolOutputProviderMetadata = toolOutputAvailable.Data?["providerMetadata"].GetProperty(ProviderId)
            ?? throw new InvalidOperationException("Expected provider metadata on tool-output-available response bridge event.");
        Assert.Equal(toolUseId, toolOutputProviderMetadata.GetProperty("tool_use_id").GetString());
        var toolOutputResults = toolOutputAvailable.Data?["output"].GetProperty("structuredContent").GetProperty("content")
            ?? throw new InvalidOperationException("Expected structured search results on tool-output-available response bridge event.");
        Assert.True(toolOutputResults.GetArrayLength() >= 1);
        Assert.Equal("2026 Iran war - Wikipedia", toolOutputResults[0].GetProperty("title").GetString());
        Assert.Equal("https://en.wikipedia.org/wiki/2026_Iran_war", toolOutputResults[0].GetProperty("url").GetString());

        var sourceUrls = responseParts
            .Where(part => part.Type == "source-url")
            .Select(part => Assert.IsType<ResponseUnknownEvent>(part).Data?["url"].GetString())
            .ToList();
        Assert.Contains("https://en.wikipedia.org/wiki/2026_Iran_war", sourceUrls);
        Assert.Contains("https://www.aljazeera.com/news/liveblog/2026/4/16/iran-war-live-pakistan-in-push-for-new-round-of-us-iran-peace-negotiations", sourceUrls);
        Assert.Contains("https://www.cnbc.com/2026/04/15/iran-war-trump-peace-deal-us-talks-stock-market-oil-prices-.html", sourceUrls);

        var bridgedText = string.Concat(
            responseParts
                .Where(part => part.Type == "text-delta")
                .Select(part => Assert.IsType<ResponseUnknownEvent>(part).Data?["delta"].GetString()));
        Assert.StartsWith("## Samenvatting: Nieuwste ontwikkelingen in de Iraanse oorlog", bridgedText);
        Assert.Contains("Iran reageerde met raket- en dronenaanvallen", bridgedText);
        Assert.Contains("Het Internationaal Monetair Fonds heeft gewaarschuwd", bridgedText);

        var finishPart = Assert.IsType<ResponseUnknownEvent>(responseParts[^1]);
        Assert.Equal(Model, finishPart.Data?["model"].GetString());
        Assert.Equal("stop", finishPart.Data?["finishReason"].GetString());
        Assert.Equal(19246, finishPart.Data?["inputTokens"].GetInt32());
        Assert.Equal(789, finishPart.Data?["outputTokens"].GetInt32());
        Assert.Equal(20035, finishPart.Data?["totalTokens"].GetInt32());
    }
}
