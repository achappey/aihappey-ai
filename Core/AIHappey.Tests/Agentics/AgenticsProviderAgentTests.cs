using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Agentics;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Agentics;

public sealed class AgenticsProviderAgentTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_agent_posts_native_payload_and_maps_response()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/agent/message", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateJsonResponse(
                """
                {
                  "response": "Here are the latest AI news...",
                  "tokensUsed": 500,
                  "toolsUsed": ["web_search"]
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(CreateAgentRequest());

        Assert.NotNull(body);
        using var requestDoc = JsonDocument.Parse(body!);
        var root = requestDoc.RootElement;
        Assert.Equal("Search for the latest AI news", root.GetProperty("message").GetString());
        Assert.Equal(1200, root.GetProperty("maxTokens").GetInt32());
        Assert.Equal("web_search", root.GetProperty("tools")[0].GetString());
        Assert.Equal("gentle", root.GetProperty("executionMode").GetString());
        Assert.Equal("assistant", root.GetProperty("context")[0].GetProperty("role").GetString());
        Assert.Equal("Previous answer", root.GetProperty("context")[0].GetProperty("content").GetString());

        Assert.Equal("agentics", response.ProviderId);
        Assert.Equal("agentics/agent", response.Model);
        Assert.Equal("completed", response.Status);
        var message = Assert.Single(response.Output?.Items ?? []);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(message.Content ?? []));
        Assert.Equal("Here are the latest AI news...", text.Text);
        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(500, usage.GetProperty("tokensUsed").GetInt32());
        Assert.Equal(500, response.Metadata?["agentics.agent.tokens_used"]);
        Assert.True((bool)(response.Metadata?["agentics.agent"] ?? false));
    }

    [Fact]
    public async Task StreamUnifiedAsync_agent_mimics_stream_from_non_streaming_response()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/agent/message", request.RequestUri?.AbsolutePath);
            Assert.Contains(request.Headers.Accept, header => header.MediaType == MediaTypeNames.Application.Json);

            return CreateJsonResponse(
                """
                {
                  "response": "Synthetic stream text",
                  "tokensUsed": 42,
                  "toolsUsed": ["bash"]
                }
                """);
        });

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "agentics",
            Model = "agent",
            Input = new AIInput { Text = "Run a sandbox check" }
        }));

        Assert.Equal(["text-start", "text-delta", "text-end", "finish"], events.Select(e => e.Event.Type).ToArray());
        Assert.Equal("Synthetic stream text", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal("agent", finish.Model);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(42, finish.TotalTokens);
        Assert.NotNull(finish.MessageMetadata);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_non_agent_model_uses_existing_chat_completion_route()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri?.AbsolutePath);
            return CreateJsonResponse(
                """
                {
                  "id": "chatcmpl_1",
                  "object": "chat.completion",
                  "created": 1730000000,
                  "model": "llama-3",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "hello" },
                      "finish_reason": "stop"
                    }
                  ]
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "agentics",
            Model = "llama-3",
            Input = new AIInput { Text = "Hello" }
        });

        Assert.Equal("agentics", response.ProviderId);
    }

    private static AIRequest CreateAgentRequest()
        => new()
        {
            ProviderId = "agentics",
            Model = "agentics/agent",
            MaxOutputTokens = 1200,
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Previous answer"
                            }
                        ]
                    },
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Search for the latest AI news"
                            }
                        ]
                    }
                ]
            },
            Metadata = new Dictionary<string, object?>
            {
                ["agentics"] = JsonSerializer.SerializeToElement(new
                {
                    tools = new[] { "web_search" },
                    executionMode = "gentle"
                }, JsonSerializerOptions.Web)
            }
        };

    private static AgenticsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new AgenticsProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static HttpResponseMessage CreateJsonResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);
        return response;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
