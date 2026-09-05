using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Epho;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Epho;

public class EphoProviderUnifiedArtifactTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_DownloadsArtifactsAsBase64()
    {
        byte[] pdf = [1, 2, 3];
        byte[] text = Encoding.UTF8.GetBytes("hello");
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/chat" => SseResponse(DoneFrame(
                "[{\"name\":\"report.pdf\",\"url\":\"https://files.test/report.pdf\",\"size\":3}," +
                "{\"name\":\"notes.txt\",\"url\":\"https://files.test/notes.txt\",\"size\":5}]")),
            "/report.pdf" => BinaryResponse(pdf, "application/pdf"),
            "/notes.txt" => BinaryResponse(text),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var response = await provider.ExecuteUnifiedAsync(Request());
        var files = response.Output!.Items!.Single().Content!.OfType<AIFileContentPart>().ToList();

        Assert.Collection(files,
            file =>
            {
                Assert.Equal("report.pdf", file.Filename);
                Assert.Equal("application/pdf", file.MediaType);
                Assert.Equal(Convert.ToBase64String(pdf), Assert.IsType<string>(file.Data));
                Assert.Equal("https://files.test/report.pdf", file.Metadata!["epho.artifact_url"]);
            },
            file =>
            {
                Assert.Equal("notes.txt", file.Filename);
                Assert.Equal("text/plain", file.MediaType);
                Assert.Equal(Convert.ToBase64String(text), Assert.IsType<string>(file.Data));
            });
    }

    [Fact]
    public async Task StreamUnifiedAsync_DownloadsArtifactAsDataUrl()
    {
        byte[] bytes = [4, 5, 6];
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/chat" => SseResponse(DoneFrame(
                "[{\"name\":\"bundle.zip\",\"url\":\"https://files.test/bundle.zip\",\"size\":3}]")),
            "/bundle.zip" => BinaryResponse(bytes, "application/zip"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var events = new List<AIStreamEvent>();
        await foreach (var streamEvent in provider.StreamUnifiedAsync(Request()))
            events.Add(streamEvent);

        var file = Assert.IsType<AIFileEventData>(events.Single(item => item.Event.Type == "file").Event.Data);
        Assert.Equal("bundle.zip", file.Filename);
        Assert.Equal("application/zip", file.MediaType);
        Assert.Equal($"data:application/zip;base64,{Convert.ToBase64String(bytes)}", file.Url);
        Assert.Equal("https://files.test/bundle.zip", file.ProviderMetadata!["epho"]["url"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, false, "status 502")]
    [InlineData(HttpStatusCode.OK, true, "empty file")]
    public async Task ExecuteUnifiedAsync_FailedArtifactDownloadFailsEntireRequest(
        HttpStatusCode statusCode,
        bool empty,
        string expectedMessage)
    {
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/api/v1/chat" => SseResponse(DoneFrame(
                "[{\"name\":\"failed.bin\",\"url\":\"https://files.test/failed.bin\"}]")),
            "/failed.bin" => new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(empty ? [] : [1])
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteUnifiedAsync(Request()));

        Assert.Contains("Epho artifact download", exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
        Assert.Contains("failed.bin", exception.Message);
    }

    private static AIRequest Request() => new()
    {
        ProviderId = "epho",
        Model = "epho/codex/gpt-5",
        Input = new AIInput { Text = "build files" }
    };

    private static string DoneFrame(string artifacts)
        => $"{{\"type\":\"done\",\"output\":\"done\",\"status\":\"completed\",\"artifacts\":{artifacts}}}";

    private static HttpResponseMessage SseResponse(string doneFrame) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"type\":\"chat\",\"chat_id\":\"chat-1\",\"turn_id\":\"turn-1\"}\n\n" +
            $"data: {doneFrame}\n\n",
            Encoding.UTF8,
            "text/event-stream")
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string? mediaType = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (mediaType is not null)
            response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private static EphoProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

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
