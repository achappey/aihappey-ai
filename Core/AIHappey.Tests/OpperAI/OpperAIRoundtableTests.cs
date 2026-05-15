using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpperAI;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpperAI;

public sealed class OpperAIRoundtableTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_roundtable_posts_native_payload_and_maps_response()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v3/roundtable", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateJsonResponse(
                """
                {
                  "id": "rt_123",
                  "data": "Final answer",
                  "meta": {
                    "resolution": "summary",
                    "models_used": ["gpt-4o", "anthropic/claude-sonnet-4-20250514"],
                    "summary_model": "gpt-4o",
                    "summary_usage": { "input_tokens": 11, "output_tokens": 7 },
                    "total_cost": 0.0123,
                    "total_duration_ms": 456,
                    "trace_uuid": "trace-1"
                  },
                  "model_results": [
                    {
                      "index": 0,
                      "model": "gpt-4o",
                      "data": "A",
                      "duration_ms": 100,
                      "cost": 0.001,
                      "usage": { "input_tokens": 5, "output_tokens": 2 }
                    }
                  ]
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRoundtableRequest());

        Assert.NotNull(body);
        using var requestDoc = JsonDocument.Parse(body!);
        var root = requestDoc.RootElement;
        Assert.Equal("Tell me the best option", root.GetProperty("input").GetString());
        Assert.Equal("Be concise", root.GetProperty("instructions").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("summary", root.GetProperty("resolution").GetString());
        Assert.Equal(2, root.GetProperty("models").GetArrayLength());
        Assert.Equal("gpt-4o", root.GetProperty("models")[0].GetProperty("name").GetString());
        Assert.Equal(0.2, root.GetProperty("models")[0].GetProperty("temperature").GetDouble());
        Assert.Equal(45000, root.GetProperty("timeout_ms").GetInt32());

        Assert.Equal("opperai", response.ProviderId);
        Assert.Equal("roundtable", response.Model);
        Assert.Equal("completed", response.Status);
        var message = Assert.Single(response.Output?.Items ?? []);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(message.Content ?? []));
        Assert.Equal("Final answer", text.Text);
        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(11, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal("summary", response.Metadata?["opperai.roundtable.resolution"]);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_non_roundtable_uses_existing_responses_route()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v3/compat/responses", request.RequestUri?.AbsolutePath);
            return CreateJsonResponse(
                """
                {
                  "id": "resp_123",
                  "object": "response",
                  "created_at": 123,
                  "status": "completed",
                  "model": "gpt-4o-mini",
                  "output": [
                    {
                      "type": "message",
                      "id": "msg_123",
                      "role": "assistant",
                      "status": "completed",
                      "content": [ { "type": "output_text", "text": "hello", "annotations": [] } ]
                    }
                  ],
                  "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "opperai",
            Model = "gpt-4o-mini",
            Input = new AIInput { Text = "Hello" }
        });

        Assert.Equal("opperai", response.ProviderId);
        Assert.Equal("gpt-4o-mini", response.Model);
    }

    [Fact]
    public async Task StreamUnifiedAsync_roundtable_posts_stream_payload_and_parses_sse()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v3/roundtable", request.RequestUri?.AbsolutePath);
            Assert.Contains(request.Headers.Accept, h => h.MediaType == "text/event-stream");
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateStreamingResponse(
                """
                data: {"delta":"Hel"}

                data: {"delta":"lo"}

                data: {"id":"rt_stream","data":"Hello","meta":{"resolution":"summary","models_used":["gpt-4o"],"total_cost":0.001,"total_duration_ms":20},"model_results":[]}

                data: [DONE]

                """);
        });

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(CreateRoundtableRequest()));

        Assert.NotNull(body);
        using var requestDoc = JsonDocument.Parse(body!);
        Assert.True(requestDoc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("Tell me the best option", requestDoc.RootElement.GetProperty("input").GetString());

        Assert.Equal(["text-start", "text-delta", "text-delta", "text-delta", "text-end", "finish"], events.Select(e => e.Event.Type).ToArray());
        Assert.Equal("Hel", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        Assert.Equal("lo", Assert.IsType<AITextDeltaEventData>(events[2].Event.Data).Delta);
        Assert.Equal("Hello", Assert.IsType<AITextDeltaEventData>(events[3].Event.Data).Delta);
        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal("roundtable", finish.Model);
        Assert.Equal(0.001m, finish.MessageMetadata?.Gateway?.Cost);
    }

    private static AIRequest CreateRoundtableRequest()
        => new()
        {
            ProviderId = "opperai",
            Model = "roundtable",
            Instructions = "Be concise",
            Input = new AIInput { Text = "Tell me the best option" },
            Metadata = new Dictionary<string, object?>
            {
                ["opperai"] = JsonSerializer.SerializeToElement(new
                {
                    models = new object[]
                    {
                        new { name = "gpt-4o", temperature = 0.2 },
                        new { name = "anthropic/claude-sonnet-4-20250514", reasoning = "low" }
                    },
                    resolution = "summary",
                    timeout_ms = 45000,
                    max_retries = 2,
                    output_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            answer = new { type = "string" }
                        }
                    }
                }, JsonSerializerOptions.Web)
            }
        };

    private static OpperAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        return new OpperAIProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            httpClientFactory);
    }

    private static HttpResponseMessage CreateJsonResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
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
