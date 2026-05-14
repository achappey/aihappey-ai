using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Venice;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Venice;

public sealed class VeniceProviderStreamTests
{
    [Fact]
    public async Task Web_search_citations_are_emitted_as_source_url_events_and_ui_parts()
    {
        var provider = new TestVeniceProvider(CreateVeniceCitationFixture());
        var request = new AIRequest
        {
            ProviderId = "venice",
            Model = "venice/kimi-k2-6",
            Stream = true
        };

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(request));
        var eventTypes = events.Select(streamEvent => streamEvent.Event.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(eventTypes, "text-start", "text-delta", "text-end", "source-url", "source-url", "finish");

        var sourceEvents = events
            .Where(streamEvent => string.Equals(streamEvent.Event.Type, "source-url", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, sourceEvents.Count);

        var firstSource = Assert.IsType<AISourceUrlEventData>(sourceEvents[0].Event.Data);
        Assert.Equal("https://example.com/one", firstSource.Url);
        Assert.Equal("Source One", firstSource.Title);
        Assert.Equal("url_citation", firstSource.Type);

        var firstProviderMetadata = Assert.Contains("venice", firstSource.ProviderMetadata ?? []);
        Assert.Equal("web_search_citation", Assert.IsType<string>(firstProviderMetadata["citation_type"]));
        Assert.Equal(1, Assert.IsType<int>(firstProviderMetadata["citation_index"]));
        Assert.True(sourceEvents[0].Metadata?.ContainsKey("chatcompletions.stream.venice_parameters") == true);

        var secondSource = Assert.IsType<AISourceUrlEventData>(sourceEvents[1].Event.Data);
        Assert.Equal("https://example.com/two", secondSource.Url);
        Assert.Equal("Source Two", secondSource.Title);

        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal(12, finish.InputTokens);
        Assert.Equal(3, finish.OutputTokens);
        Assert.Equal(15, finish.TotalTokens);

        var uiParts = events
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart("venice"))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);

        var sourceUiParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal(2, sourceUiParts.Count);
        Assert.Equal(["https://example.com/one", "https://example.com/two"], sourceUiParts.Select(part => part.Url).ToArray());
    }

    private static IReadOnlyList<ChatCompletionUpdate> CreateVeniceCitationFixture()
        =>
        [
            DeserializeChatCompletionUpdate("""
            {"id":"chatcmpl-venice-1","object":"chat.completion.chunk","created":1778768574,"model":"venice/kimi-k2-6","choices":[{"index":0,"delta":{"role":"assistant","content":"Venice answer"},"finish_reason":null}]}
            """),
            DeserializeChatCompletionUpdate("""
            {"id":"chatcmpl-venice-1","object":"chat.completion.chunk","created":1778768574,"model":"venice/kimi-k2-6","choices":[{"index":0,"delta":{"content":""},"finish_reason":"stop"}]}
            """),
            DeserializeChatCompletionUpdate("""
            {"id":"chatcmpl-venice-1","object":"chat.completion.chunk","created":1778768574,"model":"venice/kimi-k2-6","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}
            """),
            DeserializeChatCompletionUpdate("""
            {"id":"chatcmpl-venice-1","object":"chat.completion.chunk","created":1778768574,"model":"venice/kimi-k2-6","choices":[],"venice_parameters":{"enable_web_search":"on","enable_web_citations":true,"include_search_results_in_stream":true,"web_search_citations":[{"content":"First source snippet","date":"","title":"Source One","url":"https://example.com/one"},{"content":"Duplicate source snippet","date":"","title":"Source One Duplicate","url":"https://example.com/one"}]}}
            """),
            DeserializeChatCompletionUpdate("""
            {"id":"chatcmpl-venice-1","object":"chat.completion.chunk","created":1778768575,"model":"venice/kimi-k2-6","choices":[],"venice_parameters":{"enable_web_search":"on","enable_web_citations":true,"include_search_results_in_stream":true,"web_search_citations":[{"content":"Second source snippet","date":"2026-05-14","title":"Source Two","url":"https://example.com/two"},{"content":"Ignored missing url","date":"","title":"No Url"}]}}
            """)
        ];

    private static ChatCompletionUpdate DeserializeChatCompletionUpdate(string json)
        => JsonSerializer.Deserialize<ChatCompletionUpdate>(json, JsonSerializerOptions.Web)
           ?? throw new InvalidOperationException("Failed to deserialize Venice test chunk.");

    private sealed class TestVeniceProvider : VeniceProvider
    {
        private readonly IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates;

        public TestVeniceProvider(IReadOnlyList<ChatCompletionUpdate> chatCompletionUpdates)
            : base(new NullApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new TestHttpClientFactory())
            => this.chatCompletionUpdates = chatCompletionUpdates;

        public override IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
            ChatCompletionOptions options,
            CancellationToken cancellationToken = default)
            => ReplayAsync(cancellationToken);

        private async IAsyncEnumerable<ChatCompletionUpdate> ReplayAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in chatCompletionUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }
    }

    private sealed class NullApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
