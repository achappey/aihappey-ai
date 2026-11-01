using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Valyu;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Http;

namespace AIHappey.Tests.Valyu;

public sealed class ValyuProviderUnifiedTests
{
    [Fact]
    public async Task Unified_answer_preserves_provider_options_response_metadata_and_streaming()
    {
        var calls = 0;
        var provider = Assert.IsAssignableFrom<IUnifiedModelProvider>(CreateProvider(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/answer", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("X-API-Key")));
            Assert.Contains(request.Headers.Accept, header => header.MediaType == "text/event-stream");

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("question", body.RootElement.GetProperty("query").GetString());
            Assert.Equal("web", body.RootElement.GetProperty("search_type").GetString());
            Assert.Equal("short", body.RootElement.GetProperty("response_length").GetString());
            Assert.Equal("kept", body.RootElement.GetProperty("future_option").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"Valyu answer\"}}],\"search_results\":[{\"url\":\"https://example.com/source\",\"title\":\"Source\"}],\"success\":true,\"cost\":0.12,\"ai_usage\":{\"input_tokens\":3,\"output_tokens\":2},\"future_field\":{\"retained\":true}}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        }));

        var request = new AIRequest
        {
            ProviderId = "valyu",
            Model = "answer/web",
            Input = new AIInput { Text = "question" },
            Metadata = new Dictionary<string, object?>
            {
                ["valyu"] = JsonSerializer.SerializeToElement(new
                {
                    response_length = "short",
                    future_option = "kept"
                })
            }
        };

        var response = await provider.ExecuteUnifiedAsync(request);
        Assert.Equal("completed", response.Status);
        Assert.Contains("Valyu answer", JsonSerializer.Serialize(response.Output));
        Assert.Contains("future_field", JsonSerializer.Serialize(response.Metadata));
        Assert.Contains("ai_usage", JsonSerializer.Serialize(response.Metadata));

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(request))
            events.Add(item);

        Assert.Equal(2, calls);
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        Assert.Equal("finish", events[^1].Event.Type);
    }

    private static ValyuProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        => new(new TestKeyResolver(), new TestHttpClientFactory(sendAsync));

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TestHttpMessageHandler(sendAsync));
    }

    private sealed class TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }
}
