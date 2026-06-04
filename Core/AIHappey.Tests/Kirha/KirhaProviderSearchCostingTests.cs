using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Kirha;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Kirha;

public class KirhaProviderSearchCostingTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_does_not_emit_token_usage_and_adds_gateway_cost_from_consumed_credits()
    {
        var provider = CreateProvider(CreateSearchResponse());
        var response = await provider.ExecuteUnifiedAsync(CreateSearchRequest());

        Assert.Null(response.Usage);

        var usage = JsonSerializer.SerializeToElement(response.Metadata?["usage"], JsonSerializerOptions.Web);
        Assert.Equal(10, usage.GetProperty("estimated").GetInt32());
        Assert.Equal(4, usage.GetProperty("consumed").GetInt32());

        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.20m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public async Task StreamUnifiedAsync_finish_event_does_not_emit_token_counts_and_adds_gateway_cost_from_consumed_credits()
    {
        var provider = CreateProvider(CreateSearchResponse());
        var events = new List<AIStreamEvent>();

        await foreach (var update in provider.StreamUnifiedAsync(CreateSearchRequest()))
            events.Add(update);

        var finish = Assert.Single(events, static item => item.Event.Type == "finish");
        var data = Assert.IsType<AIFinishEventData>(finish.Event.Data);

        Assert.Null(data.InputTokens);
        Assert.Null(data.OutputTokens);
        Assert.Null(data.TotalTokens);
        Assert.Null(data.MessageMetadata?.InputTokens);
        Assert.Null(data.MessageMetadata?.OutputTokens);
        Assert.Null(data.MessageMetadata?.TotalTokens);
        Assert.False(data.MessageMetadata?.Usage.TryGetProperty("inputTokens", out _) ?? true);
        Assert.False(data.MessageMetadata?.Usage.TryGetProperty("outputTokens", out _) ?? true);
        Assert.False(data.MessageMetadata?.Usage.TryGetProperty("totalTokens", out _) ?? true);
        Assert.Equal(0.20m, data.MessageMetadata?.Gateway?.Cost);
    }

    private static KirhaProvider CreateProvider(string responseJson)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.kirha.com/chat/v1/search", request.RequestUri?.AbsoluteUri);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }));

        return new KirhaProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(httpClient));
    }

    private static AIRequest CreateSearchRequest()
        => new()
        {
            ProviderId = "kirha",
            Model = "kirha/search/news",
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
                                Text = "latest Kirha search cost behavior"
                            }
                        ]
                    }
                ]
            }
        };

    private static string CreateSearchResponse()
        => """
           {
             "id": "plan_123",
             "summary": "Kirha search summary.",
             "status": "completed",
             "usage": {
               "estimated": 10,
               "consumed": 4
             },
             "raw_data": [],
             "planning": {
               "status": "completed",
               "steps": []
             }
           }
           """;

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
            => Task.FromResult(responder(request));
    }
}
