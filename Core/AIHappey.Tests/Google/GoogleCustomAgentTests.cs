using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleCustomAgentTests
{
    [Fact]
    public async Task AgentDiscoveryHandlesBothBetaEnvelopesAndPagination()
    {
        var handler = new RecordingHandler([
            JsonResponse(new
            {
                agents = new object[]
                {
                    new { id = "customer-sentinel", display_name = "Customer Sentinel", description = "Checks customers", created = "2025-11-26T12:25:15Z" },
                    new { id = "", display_name = "Invalid" },
                    new { id = "bad/id", display_name = "Invalid" }
                },
                next_page_token = "second page"
            }),
            JsonResponse(new
            {
                data = new object[]
                {
                    new { id = "customer-sentinel" },
                    new { id = "agent-with-image-in-name" }
                },
                @object = "list"
            })
        ]);

        var provider = CreateProvider(handler);
        var method = typeof(GoogleAIProvider).GetMethod("ListGoogleCustomAgents", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task<List<Model>>)method.Invoke(provider, [CancellationToken.None])!;
        var agents = await task;

        Assert.Collection(agents,
            first =>
            {
                Assert.Equal("google/agents/customer-sentinel", first.Id);
                Assert.Equal("Customer Sentinel", first.Name);
                Assert.Equal("Checks customers", first.Description);
                Assert.Equal(DateTimeOffset.Parse("2025-11-26T12:25:15Z").ToUnixTimeSeconds(), first.Created);
                Assert.Equal("language", first.Type);
            },
            second =>
            {
                Assert.Equal("google/agents/agent-with-image-in-name", second.Id);
                Assert.Equal("agent-with-image-in-name", second.Name);
                Assert.Equal("language", second.Type);
            });
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("test-key", request.ApiKey);
        });
        Assert.Contains("page_token=second%20page", handler.Requests[1].Uri!.Query);
    }

    [Theory]
    [InlineData("google/agents/customer-sentinel", "customer-sentinel")]
    [InlineData("agents/customer-sentinel", "customer-sentinel")]
    [InlineData("google/models/agents/customer-sentinel", "customer-sentinel")]
    public async Task ListedAgentExecutesAsAgentWithoutManagedAgentDefaults(string model, string expectedId)
    {
        var handler = new RecordingHandler([JsonResponse(new
        {
            id = "interaction-1", agent = expectedId, status = "completed",
            steps = new[] { new { type = "model_output", content = new[] { new { type = "text", text = "Done" } } } }
        })]);
        var response = await CreateProvider(handler).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "google",
            Model = model,
            Input = new AIInput { Text = "Check" }
        });

        Assert.Equal("completed", response.Status);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(expectedId, payload.RootElement.GetProperty("agent").GetString());
        Assert.False(payload.RootElement.TryGetProperty("model", out _));
        Assert.False(payload.RootElement.TryGetProperty("generation_config", out _));
        Assert.False(payload.RootElement.TryGetProperty("environment", out _));
        Assert.False(payload.RootElement.TryGetProperty("background", out _));
        Assert.False(payload.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task BareCustomIdInAgentFieldStreamsWithoutPerRequestLookup()
    {
        var sse = "data: {\"event_type\":\"interaction.created\",\"interaction\":{\"id\":\"i-1\",\"agent\":\"customer-sentinel\",\"status\":\"in_progress\"}}\n\n"
                + "data: {\"event_type\":\"interaction.completed\",\"interaction\":{\"id\":\"i-1\",\"agent\":\"customer-sentinel\",\"status\":\"completed\"}}\n\n"
                + "data: [DONE]\n\n";
        var handler = new RecordingHandler([new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }]);
        var request = new InteractionRequest
        {
            Agent = "customer-sentinel",
            Model = "gemini-2.5-flash",
            Input = new InteractionsInput("Check"),
            GenerationConfig = new InteractionGenerationConfig(),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["generation_config"] = JsonSerializer.SerializeToElement(new { seed = 12 })
            }
        };

        var events = new List<InteractionStreamEventPart>();
        await foreach (var evt in CreateProvider(handler).GetInteractions(request))
            events.Add(evt);

        Assert.Equal(2, events.Count);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("customer-sentinel", payload.RootElement.GetProperty("agent").GetString());
        Assert.False(payload.RootElement.TryGetProperty("model", out _));
        Assert.False(payload.RootElement.TryGetProperty("generation_config", out _));
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
        Assert.Single(handler.Requests); // No agent lookup or managed-agent cleanup.
    }

    [Theory]
    [InlineData("google/agents/antigravity-custom", "antigravity-custom")]
    [InlineData("google/agents/deep-research-custom", "deep-research-custom")]
    public async Task ExplicitMarkerWinsOverManagedAgentNameHeuristics(string model, string agent)
    {
        var handler = new RecordingHandler([JsonResponse(new { agent, status = "completed" })]);
        await CreateProvider(handler).GetInteraction(new InteractionRequest
        {
            Model = model,
            Input = new InteractionsInput("Hello")
        });

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(agent, payload.RootElement.GetProperty("agent").GetString());
        Assert.False(payload.RootElement.TryGetProperty("model", out _));
        Assert.False(payload.RootElement.TryGetProperty("environment", out _));
        Assert.False(payload.RootElement.TryGetProperty("background", out _));
    }

    [Fact]
    public async Task DiscoveredAgentStreamsThroughUnifiedEntryPoint()
    {
        var sse = "data: {\"event_type\":\"interaction.created\",\"interaction\":{\"id\":\"i-1\",\"agent\":\"customer-sentinel\",\"status\":\"in_progress\"}}\n\n"
                + "data: {\"event_type\":\"step.start\",\"index\":0,\"step\":{\"type\":\"model_output\"}}\n\n"
                + "data: {\"event_type\":\"step.delta\",\"index\":0,\"delta\":{\"type\":\"text\",\"text\":\"Done\"}}\n\n"
                + "data: {\"event_type\":\"step.stop\",\"index\":0}\n\n"
                + "data: {\"event_type\":\"interaction.completed\",\"interaction\":{\"id\":\"i-1\",\"agent\":\"customer-sentinel\",\"status\":\"completed\"}}\n\n"
                + "data: [DONE]\n\n";
        var handler = new RecordingHandler([new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        }]);
        var streamEvents = new List<AIStreamEvent>();
        await foreach (var evt in CreateProvider(handler).StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "google",
            Model = "google/agents/customer-sentinel",
            Input = new AIInput { Text = "Check" }
        }))
            streamEvents.Add(evt);

        Assert.NotEmpty(streamEvents);
        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("customer-sentinel", payload.RootElement.GetProperty("agent").GetString());
        Assert.False(payload.RootElement.TryGetProperty("model", out _));
        Assert.True(payload.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OrdinaryGeminiModelStillUsesModelField()
    {
        var handler = new RecordingHandler([JsonResponse(new { model = "gemini-2.5-flash", status = "completed" })]);
        await CreateProvider(handler).GetInteraction(new InteractionRequest
        {
            Model = "gemini-2.5-flash",
            Input = new InteractionsInput("Hello")
        });

        using var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal("gemini-2.5-flash", payload.RootElement.GetProperty("model").GetString());
        Assert.False(payload.RootElement.TryGetProperty("agent", out _));
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    private static GoogleAIProvider CreateProvider(RecordingHandler handler) => new(
        new FixedApiKeyResolver(),
        new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
        NullLogger<GoogleAIProvider>.Instance,
        new FixedHttpClientFactory(new HttpClient(handler)));

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body, string? ApiKey);
}
