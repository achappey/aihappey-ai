using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.YouCom;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.YouCom;

public class YouComProviderUnifiedTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_Answer_UsesOnlyLastUserMessageTextParts()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/answer")
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"answer\":\"done\"}");
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "youcom",
            Model = "youcom/answer",
            Instructions = "very long system instructions",
            Input = new AIInput
            {
                Items =
                [
                    Message("system", "embedded system text"),
                    Message("user", "old question"),
                    Message("assistant", "old answer"),
                    new AIInputItem
                    {
                        Type = "message", Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "final part one" },
                            new AIFileContentPart { Type = "file", Data = "ignored" },
                            new AITextContentPart { Type = "text", Text = "final part two" }
                        ]
                    }
                ]
            }
        });

        var payload = JsonSerializer.Deserialize<JsonElement>(body!);
        Assert.Equal("final part one\nfinal part two", payload.GetProperty("query").GetString());
        Assert.DoesNotContain("system", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("old question", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_Research_PreservesExistingConversationPrompt()
    {
        string? body = null;
        var provider = CreateProvider(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("{\"output\":{\"content\":\"done\"}}");
        });

        await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "youcom",
            Model = "research-lite",
            Instructions = "research instructions",
            Input = new AIInput { Items = [Message("user", "first"), Message("assistant", "second"), Message("user", "third")] }
        });

        var input = JsonSerializer.Deserialize<JsonElement>(body!).GetProperty("input").GetString();
        Assert.Contains("system: research instructions", input);
        Assert.Contains("user: first", input);
        Assert.Contains("assistant: second", input);
        Assert.Contains("user: third", input);
    }

    private static AIInputItem Message(string role, string text) => new()
    {
        Type = "message", Role = role,
        Content = [new AITextContentPart { Type = "text", Text = text }]
    };

    private static YouComProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(responder));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private static HttpResponseMessage CountedBinaryResponse(ref int count, byte[] bytes, string mediaType)
    {
        count++;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private static HttpResponseMessage CountedResponse(ref int count, HttpStatusCode statusCode)
    {
        count++;
        return new HttpResponseMessage(statusCode);
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticResponseHttpMessageHandler(responder));
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
