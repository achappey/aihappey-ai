using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cortecs;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cortecs;

public sealed class CortecsProviderOcrTests
{
    [Theory]
    [InlineData("https://example.com/report.pdf?sig=a%2Bb&part=1")]
    [InlineData("http://example.com/report.pdf")]
    public async Task ExecuteOcrPassesDocumentUrlsDirectlyToCortecs(string url)
    {
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.cortecs.ai/v1/ocr", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var document = payload.RootElement.GetProperty("document");
            Assert.Equal("document_url", document.GetProperty("type").GetString());
            Assert.Equal(url, document.GetProperty("document_url").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"pages":[{"index":1,"markdown":"# Remote report","images":[]}],"usage_info":{"pages_processed":1,"credits":1}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        var result = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "cortecs",
            Model = "cortecs/mistral-ocr-4.1",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AIFileContentPart
                            {
                                Type = "file",
                                Filename = "report.pdf",
                                MediaType = "application/pdf",
                                Data = url
                            }
                        ]
                    }
                ]
            }
        });

        var returnedText = Assert.Single(result.Output!.Items!
            .Single(item => item.Type == "message").Content!.OfType<AITextContentPart>());
        Assert.Equal("# Remote report", returnedText.Text);
        Assert.DoesNotContain(url, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ExecuteOcrStillConvertsBase64DocumentsToDataUrls()
    {
        string? submittedUrl = null;
        var provider = CreateProvider(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            submittedUrl = payload.RootElement.GetProperty("document").GetProperty("document_url").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"pages":[{"index":1,"markdown":"# Base64 report","images":[]}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "cortecs",
            Model = "cortecs/mistral-ocr-4.1",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AIFileContentPart
                            {
                                Type = "file",
                                Filename = "report.pdf",
                                MediaType = "application/pdf",
                                Data = "AQID"
                            }
                        ]
                    }
                ]
            }
        });

        Assert.Equal("data:application/pdf;base64,AQID", submittedUrl);
    }

    private static CortecsProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.cortecs.ai/")
        };

        return new CortecsProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "cortecs" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
