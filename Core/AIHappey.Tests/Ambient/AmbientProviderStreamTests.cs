using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Ambient;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Ambient;

public class AmbientProviderStreamTests
{
    [Fact]
    public async Task CompleteChatStreamingAsync_PreservesUsageTrailer_AndStopsBeforeKeepalives()
    {
        var provider = CreateProvider(CreateStream(includeUsage: true));

        var updates = await CollectAsync(provider.CompleteChatStreamingAsync(CreateChatOptions()));

        Assert.Equal(3, updates.Count);
        Assert.Equal("ambient/unsloth/gpt-oss-20b-GGUF", updates[0].Model);
        Assert.Contains(updates[1].Choices.OfType<JsonElement>(), choice =>
            choice.GetProperty("finish_reason").GetString() == "stop");

        var usage = Assert.IsType<JsonElement>(updates[2].Usage);
        Assert.Equal(1073, usage.GetProperty("total_tokens").GetInt32());
        Assert.Empty(updates[2].Choices);
    }

    [Fact]
    public async Task CompleteChatStreamingAsync_StopsAtFirstKeepaliveAfterFinish_WhenUsageIsMissing()
    {
        var provider = CreateProvider(CreateStream(includeUsage: false));

        var updates = await CollectAsync(provider.CompleteChatStreamingAsync(CreateChatOptions()));

        Assert.Equal(2, updates.Count);
        Assert.Contains(updates[1].Choices.OfType<JsonElement>(), choice =>
            choice.GetProperty("finish_reason").GetString() == "stop");
    }

    [Fact]
    public async Task StreamUnifiedAsync_WithAmbientUsageTrailer_EmitsFinishAndCompletes()
    {
        var provider = CreateProvider(CreateStream(includeUsage: true));
        var request = new AIRequest
        {
            ProviderId = "ambient",
            Model = "unsloth/gpt-oss-20b-GGUF",
            Input = new AIInput { Text = "Continue" }
        };

        var events = await CollectAsync(provider.StreamUnifiedAsync(request));

        var finish = Assert.Single(events.Where(streamEvent => streamEvent.Event.Type == "finish"));
        var finishData = Assert.IsType<AIFinishEventData>(finish.Event.Data);
        Assert.Equal(771, finishData.InputTokens);
        Assert.Equal(302, finishData.OutputTokens);
        Assert.Equal(1073, finishData.TotalTokens);
    }

    private static ChatCompletionOptions CreateChatOptions() => new()
    {
        Model = "unsloth/gpt-oss-20b-GGUF",
        Messages = []
    };

    private static string CreateStream(bool includeUsage)
    {
        var usage = includeUsage
            ? """
              data: {"choices":[],"created":1786748021,"id":"chatcmpl-test","model":"unsloth/gpt-oss-20b-GGUF","object":"chat.completion.chunk","usage":{"completion_tokens":302,"prompt_tokens":771,"total_tokens":1073}}

              """
            : string.Empty;

        return string.Concat(
            "data: {\"choices\":[{\"finish_reason\":null,\"index\":0,\"delta\":{\"content\":\" verder!\"}}],\"created\":1786748021,\"id\":\"chatcmpl-test\",\"model\":\"unsloth/gpt-oss-20b-GGUF\",\"object\":\"chat.completion.chunk\"}\n\n",
            "data: {\"choices\":[{\"finish_reason\":\"stop\",\"index\":0,\"delta\":{}}],\"created\":1786748021,\"id\":\"chatcmpl-test\",\"model\":\"unsloth/gpt-oss-20b-GGUF\",\"object\":\"chat.completion.chunk\"}\n\n",
            usage,
            ": keepalive\n\n",
            ": keepalive\n\n");
    }

    private static AmbientProvider CreateProvider(string body)
    {
        var handler = new StaticResponseHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return response;
        });

        return new AmbientProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
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
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
