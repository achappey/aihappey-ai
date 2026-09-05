using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ShadowOS;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ShadowOS;

public class ShadowOSProviderUnifiedTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_DownloadsAllFilesAsBase64AndUsesResponseMediaType()
    {
        byte[] first = [1, 2, 3, 4];
        byte[] second = Encoding.UTF8.GetBytes("report");
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/agent" => AgentResponse("""
                [{"name":"demo.xlsx","download_url":"https://files.test/demo.xlsx","mime":"application/wrong"},
                 {"name":"report.txt","download_url":"https://files.test/report.txt","mime":"text/plain"}]
                """),
            "/demo.xlsx" => BinaryResponse(first, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "/report.txt" => BinaryResponse(second),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var response = await provider.ExecuteUnifiedAsync(Request());
        var files = response.Output!.Items!.Single().Content!.OfType<AIFileContentPart>().ToList();

        Assert.Collection(files,
            file =>
            {
                Assert.Equal("demo.xlsx", file.Filename);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.MediaType);
                Assert.Equal(Convert.ToBase64String(first), Assert.IsType<string>(file.Data));
                Assert.Equal("https://files.test/demo.xlsx", file.Metadata!["shadowos.file.download_url"]);
            },
            file =>
            {
                Assert.Equal("report.txt", file.Filename);
                Assert.Equal("text/plain", file.MediaType);
                Assert.Equal(Convert.ToBase64String(second), Assert.IsType<string>(file.Data));
            });
    }

    [Fact]
    public async Task StreamUnifiedAsync_EmitsDownloadedFileAsDataUrlWithOctetStreamFallback()
    {
        byte[] bytes = [5, 6, 7];
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/agent" => AgentResponse(
                "[{\"name\":\"artifact.bin\",\"download_url\":\"https://files.test/artifact.bin\"}]"),
            "/artifact.bin" => BinaryResponse(bytes),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var events = new List<AIStreamEvent>();
        await foreach (var streamEvent in provider.StreamUnifiedAsync(Request()))
            events.Add(streamEvent);

        var file = Assert.IsType<AIFileEventData>(events.Single(item => item.Event.Type == "file").Event.Data);
        Assert.Equal("application/octet-stream", file.MediaType);
        Assert.Equal($"data:application/octet-stream;base64,{Convert.ToBase64String(bytes)}", file.Url);
        Assert.Equal("https://files.test/artifact.bin", file.ProviderMetadata!["shadowos"]["download_url"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone, false, "status 410")]
    [InlineData(HttpStatusCode.OK, true, "empty file")]
    public async Task ExecuteUnifiedAsync_FailedArtifactDownloadFailsEntireRequest(
        HttpStatusCode statusCode,
        bool empty,
        string expectedMessage)
    {
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/agent" => AgentResponse(
                "[{\"name\":\"expired.zip\",\"download_url\":\"https://files.test/expired.zip\",\"mime\":\"application/zip\"}]"),
            "/expired.zip" => new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(empty ? [] : [1])
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteUnifiedAsync(Request()));

        Assert.Contains("Shadow-OS file download", exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
        Assert.Contains("expired.zip", exception.Message);
    }

    private static AIRequest Request() => new()
    {
        ProviderId = "shadowos",
        Model = "shadowos/agent",
        Input = new AIInput
        {
            Text = "build a file",
            Metadata = new Dictionary<string, object?> { ["session_id"] = "test-session" }
        }
    };

    private static ShadowOSProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage AgentResponse(string filesJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"{{\"answer\":\"done\",\"request_id\":\"request-1\",\"session_id\":\"test-session\",\"files\":{filesJson}}}",
            Encoding.UTF8,
            "application/json")
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string? mediaType = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (mediaType is not null)
            response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
