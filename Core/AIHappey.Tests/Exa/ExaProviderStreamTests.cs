using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Exa;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Exa;

public sealed class ExaProviderStreamTests
{
    [Fact]
    public async Task Search_json_fallback_stream_emits_text_sources_and_finish_for_chat_api()
    {
        var provider = CreateProvider(request =>
        {
            Assert.EndsWith("/search", request.RequestUri?.AbsolutePath);
            return CreateJsonResponse(
                """
                {
                  "requestId": "e265cc5eab36d00e2547bad8889a4c0d",
                  "resolvedSearchType": "auto",
                  "results": [
                    {
                      "id": "https://www.fakton.com/ondernemingen/fakton-consultancy/",
                      "title": "Fakton Consultancy - Fakton",
                      "url": "https://www.fakton.com/ondernemingen/fakton-consultancy/",
                      "publishedDate": "2021-03-24T08:24:00.000Z",
                      "highlights": [
                        "Lees het laatste nieuws over Fakton Consultancy"
                      ]
                    },
                    {
                      "id": "https://www.fakton.com/fakton-kwartaalupdate/",
                      "title": "Fakton kwartaalupdate - Fakton",
                      "url": "https://www.fakton.com/fakton-kwartaalupdate/",
                      "publishedDate": "2025-12-05T09:37:33.000Z",
                      "highlights": [
                        "De vastgoedwaardeketen heeft een centrale rol in de Fakton Training vastgoed- en gebiedsontwikkeling."
                      ]
                    }
                  ],
                  "searchTime": 1553,
                  "costDollars": {
                    "total": 0.007,
                    "search": {
                      "neural": 0.007
                    }
                  }
                }
                """);
        });

        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(CreateChatRequest("exa/auto", "Fakton")));

        Assert.Equal(
            ["text-start", "text-delta", "text-end", "source-url", "source-url", "finish"],
            uiParts.Select(part => part.Type).ToArray());

        var textPart = Assert.IsType<TextDeltaUIMessageStreamPart>(uiParts[1]);
        Assert.Contains("[Fakton Consultancy - Fakton](https://www.fakton.com/ondernemingen/fakton-consultancy/)", textPart.Delta);
        Assert.Contains("Lees het laatste nieuws over Fakton Consultancy", textPart.Delta);
        Assert.Contains("[Fakton kwartaalupdate - Fakton](https://www.fakton.com/fakton-kwartaalupdate/)", textPart.Delta);

        var sourceParts = uiParts.OfType<SourceUIPart>().ToList();
        Assert.Equal(2, sourceParts.Count);
        Assert.Equal("https://www.fakton.com/ondernemingen/fakton-consultancy/", sourceParts[0].Url);
        Assert.Equal("https://www.fakton.com/fakton-kwartaalupdate/", sourceParts[1].Url);

        var finishPart = Assert.IsType<FinishUIPart>(uiParts[^1]);
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("exa/auto", finishPart.MessageMetadata?.Model);
        Assert.Equal(0.007m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public async Task Search_json_fallback_without_text_does_not_emit_empty_text_shells()
    {
        var provider = CreateProvider(_ => CreateJsonResponse(
            """
            {
              "requestId": "empty-search",
              "resolvedSearchType": "auto",
              "results": [],
              "costDollars": {
                "total": 0.001
              }
            }
            """));

        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(CreateChatRequest("exa/auto", "empty")));

        Assert.Equal(["finish"], uiParts.Select(part => part.Type).ToArray());

        var finishPart = Assert.IsType<FinishUIPart>(uiParts.Single());
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal(0.001m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    [Fact]
    public async Task Answer_sse_stream_remains_unchanged()
    {
        var provider = CreateProvider(request =>
        {
            Assert.EndsWith("/answer", request.RequestUri?.AbsolutePath);
            return CreateStreamingResponse(
                """
                data: {"answer":"Hello from answer","costDollars":{"total":0.002}}

                data: [DONE]

                """);
        });

        var uiParts = await FixtureAssertions.CollectAsync(provider.StreamAsync(CreateChatRequest("exa/answer", "hello")));

        Assert.Equal(["text-start", "text-delta", "text-end", "finish"], uiParts.Select(part => part.Type).ToArray());

        var textPart = Assert.IsType<TextDeltaUIMessageStreamPart>(uiParts[1]);
        Assert.Equal("Hello from answer", textPart.Delta);

        var finishPart = Assert.IsType<FinishUIPart>(uiParts[^1]);
        Assert.Equal("stop", finishPart.FinishReason);
        Assert.Equal("exa/answer", finishPart.MessageMetadata?.Model);
        Assert.Equal(0.002m, finishPart.MessageMetadata?.Gateway?.Cost);
    }

    private static ExaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        return new ExaProvider(new StaticApiKeyResolver(), httpClientFactory);
    }

    private static ChatRequest CreateChatRequest(string model, string prompt)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Model = model,
            Messages =
            [
                new UIMessage
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = prompt }]
                }
            ]
        };

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
