using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Anthropic;

namespace AIHappey.Tests.Anthropic;

public sealed class AnthropicProviderModelsTests
{
    [Fact]
    public async Task ListModels_FollowsAnthropicModelCursorPagination()
    {
        var modelRequests = new List<HttpRequestMessage>();
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                modelRequests.Add(request);
                Assert.Equal("test-api-key", request.Headers.GetValues("x-api-key").Single());
                Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());

                return modelRequests.Count switch
                {
                    1 => JsonResponse(new
                    {
                        data = new[]
                        {
                            new
                            {
                                id = "claude-test-model-1",
                                display_name = "Claude Test Model One",
                                max_input_tokens = 200000,
                                max_tokens = 8192,
                                created_at = "2025-01-01T00:00:00Z"
                            }
                        },
                        has_more = true,
                        last_id = "claude-test-model-1"
                    }),
                    2 => JsonResponse(new
                    {
                        data = new[] { new { id = "claude-test-model-2", created_at = "2025-02-01T00:00:00Z" } },
                        has_more = false,
                        last_id = "claude-test-model-2"
                    }),
                    _ => throw new InvalidOperationException("Unexpected extra model request.")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var provider = new AnthropicProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/") }));

        var models = (await provider.ListModels()).ToList();

        Assert.Equal(2, modelRequests.Count);
        Assert.Equal("?limit=1000", modelRequests[0].RequestUri?.Query);
        Assert.Equal("?limit=1000&after_id=claude-test-model-1", modelRequests[1].RequestUri?.Query);
        var firstModel = Assert.Single(models, model => model.Id == "anthropic/claude-test-model-1");
        Assert.Equal("Claude Test Model One", firstModel.Name);
        Assert.Equal(200000, firstModel.ContextWindow);
        Assert.Equal(8192, firstModel.MaxTokens);
        Assert.Contains(models, model => model.Name == "claude-test-model-2");
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
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
