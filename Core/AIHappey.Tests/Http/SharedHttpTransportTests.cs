using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Antimatter;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Http;

public sealed class SharedHttpTransportTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Responses_merge_native_headers_once_with_explicit_transport_headers_winning(
        bool streaming, bool explicitHeaders)
    {
        var inherited = new Dictionary<string, string>
        {
            ["X-Native-Option"] = "inherited",
            ["X-Request-Id"] = "correlation",
            ["X-Api-Key"] = "inherited-native-key",
            ["X-Antimatter-Key"] = "selected-SECRET",
            ["X-Anthropic-Key"] = "unrelated-SECRET"
        };
        var configured = new Dictionary<string, string>
        {
            ["x-native-option"] = "configured",
            ["x-api-key"] = "configured-native-key",
            ["X-Antimatter-Key"] = "configured-provider-key"
        };
        var inheritedSnapshot = inherited.ToArray();
        var configuredSnapshot = configured.ToArray();
        using var http = CreateClient(request =>
        {
            Assert.Equal(explicitHeaders ? "configured" : "inherited",
                Assert.Single(request.Headers.GetValues("X-Native-Option")));
            Assert.Equal(explicitHeaders ? "configured-native-key" : "inherited-native-key",
                Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.Equal("correlation", Assert.Single(request.Headers.GetValues("X-Request-Id")));
            Assert.False(request.Headers.Contains("X-Anthropic-Key"));
            if (explicitHeaders)
                Assert.Equal("configured-provider-key", Assert.Single(request.Headers.GetValues("X-Antimatter-Key")));
            else
                Assert.False(request.Headers.Contains("X-Antimatter-Key"));
            Assert.Equal("configured-bearer", request.Headers.Authorization?.Parameter);
            return ResponseSuccess(streaming);
        });
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "configured-bearer");
        var options = new ResponseRequest { Model = "antimatter/vendor/model", Headers = inherited };

        if (streaming)
            await Drain(http.GetResponsesUpdates(options, "antimatter", headers: explicitHeaders ? configured : null));
        else
            await http.GetResponses(options, "antimatter", headers: explicitHeaders ? configured : null);

        Assert.Equal(inheritedSnapshot, inherited.ToArray());
        Assert.Equal(configuredSnapshot, configured.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_core_wrapper_does_not_duplicate_request_headers(bool streaming)
    {
        using var http = CreateClient(request =>
        {
            Assert.Equal("required", Assert.Single(request.Headers.GetValues("X-Antimatter-Private")));
            Assert.Equal("native-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
            Assert.False(request.Headers.Contains("X-Antimatter-Key"));
            Assert.False(request.Headers.Contains("X-Anthropic-Key"));
            return ResponseSuccess(streaming);
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new AntimatterProvider(new KeyResolver(), new ClientFactory(http), new AsyncCacheHelper(cache));
        var headers = new Dictionary<string, string>
        {
            ["X-Antimatter-Private"] = "required",
            ["X-Api-Key"] = "native-key",
            ["X-Antimatter-Key"] = "selected-SECRET",
            ["X-Anthropic-Key"] = "unrelated-SECRET"
        };
        var snapshot = headers.ToArray();
        var options = new ResponseRequest { Model = "antimatter/vendor/model", Headers = headers };

        if (streaming)
            await Drain(provider.ResponsesStreamingAsync(options));
        else
            await provider.ResponsesAsync(options);

        Assert.Equal(snapshot, headers.ToArray());
    }

    public static IEnumerable<object[]> HttpErrors()
    {
        foreach (var transport in new[] { "chat", "messages", "responses" })
        foreach (var streaming in new[] { false, true })
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        foreach (var body in new[]
        {
            """{"error":{"message":"capacity busy"}}""",
            """{"error":"capacity busy"}""",
            """{"message":"capacity busy"}""",
            "<html>unavailable</html>",
            ""
        })
            yield return [transport, streaming, status, body];
    }

    [Theory]
    [MemberData(nameof(HttpErrors))]
    public async Task Http_errors_preserve_status_and_existing_message_behavior(
        string transport, bool streaming, HttpStatusCode status, string body)
    {
        using var http = CreateClient(_ => new HttpResponseMessage(status)
        {
            ReasonPhrase = "Upstream failure",
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            switch (transport, streaming)
            {
                case ("chat", false):
                    await http.GetChatCompletion(new ChatCompletionOptions { Model = "model" }, "test");
                    break;
                case ("chat", true):
                    await Drain(http.GetChatCompletionUpdates(new ChatCompletionOptions { Model = "model" }, "test"));
                    break;
                case ("messages", false):
                    await http.PostMessages(new MessagesRequest { Model = "model" }, "test");
                    break;
                case ("messages", true):
                    await Drain(http.PostMessagesStreaming(new MessagesRequest { Model = "model" }, "test"));
                    break;
                case ("responses", false):
                    await http.GetResponses(new ResponseRequest { Model = "model" }, "test");
                    break;
                case ("responses", true):
                    await Drain(http.GetResponsesUpdates(new ResponseRequest { Model = "model" }, "test"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(transport));
            }
        });

        Assert.Equal(status, error.StatusCode);
        var expectedMessage = string.IsNullOrWhiteSpace(body)
            ? $"HTTP {(int)status} Upstream failure"
            : body.StartsWith('{') ? "capacity busy" : body;
        Assert.Equal(expectedMessage, error.Message);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new Handler(respond)) { BaseAddress = new Uri("https://provider.test/") };

    private static HttpResponseMessage ResponseSuccess(bool streaming)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(streaming ? "data: [DONE]\n\n" : """{"id":"test","model":"model","output":[]}""",
                Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
        };

    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var item in stream) { }
    }

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
