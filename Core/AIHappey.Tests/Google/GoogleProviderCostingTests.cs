using System.Net;
using System.Text;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public class GoogleProviderCostingTests
{
    [Fact]
    public async Task ResponsesAsync_uses_response_service_tier_for_gateway_cost_when_request_tier_differs()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "object": "interaction",
                  "id": "int-standard-1",
                  "status": "completed",
                  "model": "gemini-2.5-flash",
                  "service_tier": "standard",
                  "usage": {
                    "total_input_tokens": 1000,
                    "total_output_tokens": 500,
                    "total_tokens": 1500
                  },
                  "steps": []
                }
                """)
            }
        ]);

        var provider = CreateProvider(handler);

        var result = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "google/gemini-2.5-flash",
            ServiceTier = "flex",
            Input = new ResponseInput("hello")
        });

        var gateway = Assert.IsType<Dictionary<string, object?>>(result.Metadata?["gateway"]);
        Assert.Equal(0.00155m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public async Task ResponsesStreamingAsync_enriches_completed_response_gateway_cost_from_google_priority_tier()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = SseContent(
                    new
                    {
                        event_type = "interaction.created",
                        event_id = "event-created-1",
                        interaction = new
                        {
                            id = "int-stream-1",
                            status = "in_progress",
                            model = "gemini-2.5-flash",
                            service_tier = "priority"
                        }
                    },
                    new
                    {
                        event_type = "interaction.completed",
                        event_id = "event-completed-1",
                        interaction = new
                        {
                            id = "int-stream-1",
                            status = "completed",
                            model = "gemini-2.5-flash",
                            service_tier = "priority",
                            usage = new
                            {
                                total_input_tokens = 1000,
                                total_output_tokens = 500,
                                total_tokens = 1500
                            },
                            steps = Array.Empty<object>()
                        }
                    })
            }
        ]);

        var provider = CreateProvider(handler);

        var parts = new List<ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new ResponseRequest
                       {
                           Model = "google/gemini-2.5-flash",
                           Input = new ResponseInput("hello")
                       }))
        {
            parts.Add(part);
        }

        var completed = Assert.IsType<ResponseCompleted>(parts.Single(p => p is ResponseCompleted));
        var gateway = Assert.IsType<Dictionary<string, object?>>(completed.Response.Metadata?["gateway"]);
        Assert.Equal(0.00279m, Assert.IsType<decimal>(gateway["cost"]));
    }

    private static GoogleAIProvider CreateProvider(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new GoogleAIProvider(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(httpClient));
    }

    private static StringContent JsonContent(string payload)
        => new(payload, Encoding.UTF8, "application/json");

    private static StringContent SseContent(params object[] events)
    {
        var lines = events
            .Select(e => $"data: {System.Text.Json.JsonSerializer.Serialize(e, System.Text.Json.JsonSerializerOptions.Web)}")
            .Append("data: [DONE]");

        return new StringContent(string.Join("\n\n", lines), Encoding.UTF8, "text/event-stream");
    }

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "AIzaSyDUMMYKEY1234567890123456789012345";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(IEnumerable<HttpResponseMessage> queuedResponses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(queuedResponses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(responses.TryDequeue(out var response), $"No response queued for {request.Method} {request.RequestUri}.");
            return Task.FromResult(response);
        }
    }
}
