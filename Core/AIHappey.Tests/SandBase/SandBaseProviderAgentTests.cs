using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SandBase;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SandBase;

public class SandBaseProviderAgentTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_exposes_normalized_usage()
    {
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/sessions")
                return JsonResponse(new { id = "session-1", agent_id = "agent-1" });
            if (request.Method == HttpMethod.Post && path == "/v1/sessions/session-1/events")
                return JsonResponse(new { data = new[] { new { id = "user-event" } } });
            if (request.Method == HttpMethod.Get && path == "/v1/sessions/session-1/events")
                return JsonResponse(new
                {
                    data = new object[]
                    {
                        new { id = "user-event", type = "user.message" },
                        new { id = "message-event", type = "agent.message", content = "Hello", tokens_in = 12, tokens_out = 8 },
                        new { id = "terminal-event", type = "session.status_idle" }
                    }
                });
            if (request.Method == HttpMethod.Get && path == "/v1/sessions/session-1")
                return JsonResponse(new { id = "session-1", agent_id = "agent-1" });
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sandbase",
            Model = "sandbase/agent/agent-1",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "user",
                    Content = [new AITextContentPart { Type = "text", Text = "Say hello" }]
                }]
            }
        });

        Assert.NotNull(response.NormalizedUsage);
        Assert.Equal(12, response.NormalizedUsage.InputTokens);
        Assert.Equal(8, response.NormalizedUsage.OutputTokens);
        Assert.Equal(20, response.NormalizedUsage.TotalTokens);
    }

    private static SandBaseProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(object value)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
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
