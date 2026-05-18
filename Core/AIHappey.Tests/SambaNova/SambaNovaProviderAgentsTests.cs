using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SambaNova;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SambaNova;

public sealed class SambaNovaProviderAgentsTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_mainagent_uses_non_interactive_endpoint_by_default()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("chat.sambanova.ai", request.RequestUri?.Host);
            Assert.Equal("/api/agent/mainagent", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateJsonResponse(
                """
                {
                  "status": "success",
                  "result": "I've created the bar chart...",
                  "artifacts": ["file-id-123"],
                  "thread_id": "thread-abc"
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sambanova",
            Model = "sambanova/agent/mainagent",
            Input = new AIInput { Text = "Create a bar chart for Q1 sales" }
        });

        Assert.NotNull(body);
        using var requestDoc = JsonDocument.Parse(body!);
        Assert.Equal("Create a bar chart for Q1 sales", requestDoc.RootElement.GetProperty("prompt").GetString());
        Assert.False(requestDoc.RootElement.TryGetProperty("resume", out _));

        Assert.Equal("sambanova", response.ProviderId);
        Assert.Equal("sambanova/agent/mainagent", response.Model);
        Assert.Equal("completed", response.Status);
        Assert.Equal("thread-abc", response.Metadata?["sambanova.agent.thread_id"]);
        Assert.False((bool)(response.Metadata?["sambanova.agent.interactive"] ?? true));

        var message = Assert.Single(response.Output?.Items ?? []);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(message.Content ?? []));
        Assert.Equal("I've created the bar chart...", text.Text);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_uses_interactive_endpoint_when_resume_metadata_is_present()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/agent/mainagent/interactive", request.RequestUri?.AbsolutePath);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateJsonResponse(
                """
                {
                  "status": "success",
                  "result": "Follow-up chart complete.",
                  "artifacts": [],
                  "thread_id": "thread-abc"
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sambanova",
            Model = "sambanova/agent/mainagent",
            Input = new AIInput { Text = "Now create a pie chart" },
            Metadata = new Dictionary<string, object?>
            {
                ["sambanova"] = JsonSerializer.SerializeToElement(new
                {
                    resume = true,
                    thread_id = "thread-abc"
                }, JsonSerializerOptions.Web)
            }
        });

        using var requestDoc = JsonDocument.Parse(body!);
        Assert.True(requestDoc.RootElement.GetProperty("resume").GetBoolean());
        Assert.Equal("thread-abc", requestDoc.RootElement.GetProperty("thread_id").GetString());
        Assert.True((bool)(response.Metadata?["sambanova.agent.interactive"] ?? false));
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_datascience_sends_multipart_files_and_maps_html_report()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/agent/datascience", request.RequestUri?.AbsolutePath);
            Assert.StartsWith("multipart/form-data", request.Content?.Headers.ContentType?.MediaType);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();

            return CreateJsonResponse(
                """
                {
                  "content_type": "text/html",
                  "title": "Data Science Analysis Report",
                  "html": "<html><body><h1>EDA</h1></body></html>"
                }
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "sambanova",
            Model = "sambanova/agent/datascience",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Perform exploratory data analysis"
                            },
                            new AIFileContentPart
                            {
                                Type = "file",
                                Filename = "dataset.csv",
                                MediaType = "text/csv",
                                Data = "quarter,sales\nQ1,100"
                            }
                        ]
                    }
                ]
            }
        });

        Assert.Contains("Perform exploratory data analysis", body);
        Assert.Contains("dataset.csv", body);
        Assert.Contains("quarter,sales", body);

        Assert.Equal("completed", response.Status);
        Assert.Equal("Data Science Analysis Report", response.Metadata?["sambanova.agent.title"]);
        var message = Assert.Single(response.Output?.Items ?? []);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(message.Content ?? []));
        Assert.Contains("<h1>EDA</h1>", text.Text);
    }

    [Fact]
    public async Task StreamUnifiedAsync_agent_mimics_stream_from_agent_response()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/agent/financialanalysis", request.RequestUri?.AbsolutePath);
            return CreateJsonResponse(
                """
                {
                  "status": "success",
                  "result": "AAPL shows strong revenue momentum.",
                  "artifacts": [],
                  "thread_id": "thread-fin"
                }
                """);
        });

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "sambanova",
            Model = "sambanova/agent/financialanalysis",
            Input = new AIInput { Text = "Analyze AAPL" }
        }));

        Assert.Equal(["text-start", "text-delta", "text-end", "finish"], events.Select(e => e.Event.Type).ToArray());
        Assert.Equal("AAPL shows strong revenue momentum.", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal("sambanova/agent/financialanalysis", finish.Model);
    }

    private static SambaNovaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new SambaNovaProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
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
