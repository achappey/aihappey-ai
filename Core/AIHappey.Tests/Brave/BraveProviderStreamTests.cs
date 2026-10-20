using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Brave;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Brave;

public sealed class BraveProviderStreamTests
{
    [Fact]
    public async Task Split_duplicate_citation_tags_emit_one_source_part()
    {
        const string citation = """{"number":1,"url":"https://example.com/source","title":"Source"}""";
        var chunks = new[]
        {
            CreateChunk("Answer "),
            CreateChunk("<cita"),
            CreateChunk($"tion>{citation}</citation>"),
            CreateChunk($"<citation>{citation}</citation>"),
            """{"id":"chatcmpl-test","model":"brave","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}"""
        };
        var responseBody = string.Join(
            "\n\n",
            chunks.Select(chunk => $"data: {chunk}").Append("data: [DONE]")) + "\n\n";
        var provider = CreateProvider(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
        });

        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(CreateChatRequest()))
            parts.Add(part);

        var text = string.Concat(parts.OfType<TextDeltaUIMessageStreamPart>().Select(part => part.Delta));
        Assert.Equal("Answer ", text);

        var source = Assert.Single(parts.OfType<SourceUIPart>());
        Assert.Equal("https://example.com/source", source.Url);
        Assert.Equal("Source", source.Title);
    }

    private static string CreateChunk(string content)
        => JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            model = "brave",
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { content },
                    finish_reason = (string?)null
                }
            }
        });

    private static ChatRequest CreateChatRequest()
        => new()
        {
            Id = "chat-test",
            Model = "brave/brave",
            Messages =
            [
                new UIMessage
                {
                    Id = "user-message",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "question" }]
                }
            ]
        };

    private static BraveProvider CreateProvider(HttpResponseMessage response)
        => new(
            new TestApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(new HttpClient(new TestHttpMessageHandler(response))));

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
