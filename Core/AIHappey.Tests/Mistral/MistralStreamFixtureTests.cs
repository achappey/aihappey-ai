using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Mistral;

public sealed class MistralStreamFixtureTests
{
    private const string BasicConversationStreamFixturePath = "Fixtures/mistral/raw/basic-conversation-stream.jsonl";
    private const string ConversationWithWebSearchToolCallsFixturePath = "Fixtures/mistral/raw/conversation-with-web-search-tool-calls.jsonl";

    [Fact]
    public async Task Raw_conversation_stream_fixture_populates_finish_message_metadata_model_from_stream_model()
    {
        var provider = CreateProviderFromFixture(BasicConversationStreamFixturePath);
        var request = new AIRequest
        {
            ProviderId = "mistral",
            Model = "mistral-small-latest",
            Input = new AIInput
            {
                Text = "Hoi"
            }
        };

        var unifiedEvents = new List<AIStreamEvent>();
        await foreach (var streamEvent in provider.StreamUnifiedAsync(request))
            unifiedEvents.Add(streamEvent);

        var finishEvent = unifiedEvents.Single(streamEvent => streamEvent.Event.Type == "finish");
        var finishData = Assert.IsType<AIFinishEventData>(finishEvent.Event.Data);

        Assert.Equal("mistral-small-2603", finishData.Model);
        Assert.NotNull(finishData.MessageMetadata);
        Assert.Equal("mistral-small-2603", finishData.MessageMetadata?.Model);
        Assert.Equal(1191, finishData.MessageMetadata?.Usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(16, finishData.MessageMetadata?.Usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(1207, finishData.MessageMetadata?.Usage.GetProperty("total_tokens").GetInt32());

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "text-start" or "text-delta" or "text-end" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart("mistral"))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);

        var finishPart = Assert.IsType<FinishUIPart>(uiParts[^1]);
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("mistral/mistral-small-2603", finishPart.MessageMetadata?.Model);
        Assert.Equal(1191, finishPart.MessageMetadata?.Usage.PromptTokens);
        Assert.Equal(16, finishPart.MessageMetadata?.Usage.CompletionTokens);
        Assert.Equal(1207, finishPart.MessageMetadata?.Usage.TotalTokens);
    }

    [Fact]
    public async Task Conversation_stream_fixture_filters_invalid_tool_reference_urls_from_source_url_ui_parts()
    {
        var provider = CreateProviderFromFixture(ConversationWithWebSearchToolCallsFixturePath);
        var request = new AIRequest
        {
            ProviderId = "mistral",
            Model = "mistral-small-latest",
            Input = new AIInput
            {
                Text = "Wat is het laatste nieuws over de oorlog in Iran?"
            }
        };

        var unifiedEvents = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(request));

        var uiParts = unifiedEvents
            .Where(streamEvent => streamEvent.Event.Type is "source-url" or "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart("mistral"))
            .ToList();

        FixtureAssertions.AssertAllSourceUrlsAreValid(uiParts);

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal(5, sourceParts.Count);
        Assert.Contains(sourceParts, part => part.Url == "https://en.wikipedia.org/wiki/2026_Iran_war");
        Assert.Contains(sourceParts, part => part.Url == "https://www.ewmagazine.nl/buitenland/blog/2026/02/iran-vs-oorlog-donald-trump-ali-khamenei-israel-1543634/");
        Assert.Contains(sourceParts, part => part.Url == "https://www.volkskrant.nl/buitenland/lees-hier-het-volledige-liveblog-over-de-oorlog-in-het-midden-oosten-van-30-maart-tot-en-met-9-april-2026-terug~b28549d00/");
        Assert.Contains(sourceParts, part => part.Url == "https://understandingwar.org/research/middle-east/iran-update-special-report-april-15-2026/");
        Assert.Contains(sourceParts, part => part.Url == "https://nl.wikipedia.org/wiki/Iranoorlog_2026");
        Assert.DoesNotContain(sourceParts, part => part.Url == "news-afp-20260408-aa9736d0");
        Assert.DoesNotContain(sourceParts, part => part.Url == "news-afp-20260415-35fb497e");
        Assert.DoesNotContain(sourceParts, part => part.Url == "news-afp-20260415-69f57e86");
        Assert.DoesNotContain(sourceParts, part => part.Url == "news-afp-20260416-9b3c2d42");
        Assert.DoesNotContain(sourceParts, part => part.Url == "news-afp-20260416-6180416b");
    }

    private static MistralProvider CreateProviderFromFixture(string fixturePath)
    {
        var fixtureText = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(fixturePath));
        var handler = new StaticResponseHttpMessageHandler(_ => CreateStreamingResponse(fixtureText));
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new MistralProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpResponseMessage CreateStreamingResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
