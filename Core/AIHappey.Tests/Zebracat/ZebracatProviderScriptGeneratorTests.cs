using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Zebracat;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Zebracat;

public sealed class ZebracatProviderScriptGeneratorTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_uses_latest_user_text_and_raw_provider_options()
    {
        string? requestBody = null;
        string? apiKey = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/script_generator", request.RequestUri?.AbsolutePath);
            apiKey = request.Headers.TryGetValues("X-API-KEY", out var values) ? Assert.Single(values) : null;
            requestBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { script = "Generated narration", request_id = "zc-123", credits = 4 });
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest());

        Assert.Equal("test-api-key", apiKey);
        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.Equal("Latest idea part one\nLatest idea part two", payload.RootElement.GetProperty("idea").GetString());
        Assert.Equal(60, payload.RootElement.GetProperty("duration").GetInt32());
        Assert.Equal("spanish", payload.RootElement.GetProperty("language").GetString());
        Assert.Equal("inspiring", payload.RootElement.GetProperty("mood").GetString());
        Assert.Equal("educational", payload.RootElement.GetProperty("prompt_style").GetString());
        Assert.Equal("preserved", payload.RootElement.GetProperty("future_option").GetString());

        Assert.Equal("zebracat", response.ProviderId);
        Assert.Equal("zebracat/script-generator", response.Model);
        Assert.Equal("completed", response.Status);
        Assert.Equal("Generated narration", Assert.IsType<AITextContentPart>(
            Assert.Single(Assert.Single(response.Output!.Items!).Content!)).Text);
        var raw = Assert.IsType<JsonElement>(response.Metadata!["zebracat.response.raw"]);
        Assert.Equal("zc-123", raw.GetProperty("request_id").GetString());
        Assert.Equal(4, raw.GetProperty("credits").GetInt32());
    }

    [Fact]
    public async Task StreamUnifiedAsync_mimics_text_stream_in_order()
    {
        var provider = CreateProvider(_ => JsonResponse(new { script = "Generated narration", trace = "abc" }));
        var events = new List<AIStreamEvent>();

        await foreach (var streamEvent in provider.StreamUnifiedAsync(CreateRequest()))
            events.Add(streamEvent);

        Assert.Equal(["text-start", "text-delta", "text-end", "finish"], events.Select(value => value.Event.Type));
        Assert.Equal("Generated narration", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        Assert.All(events, value => Assert.True(value.Metadata?.ContainsKey("zebracat.response.raw")));
        Assert.Equal("zebracat/script-generator", Assert.IsType<AIFinishEventData>(events[3].Event.Data).Model);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_rejects_unknown_model_before_http_call()
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return JsonResponse(new { script = "unused" });
        });
        var request = CreateRequest("zebracat/not-script-generator");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(request));

        Assert.False(called);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_includes_status_and_provider_body_in_error()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent("{\"error\":\"insufficient balance\"}", Encoding.UTF8, MediaTypeNames.Application.Json)
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteUnifiedAsync(CreateRequest()));

        Assert.Contains("402", error.Message, StringComparison.Ordinal);
        Assert.Contains("insufficient balance", error.Message, StringComparison.Ordinal);
    }

    private static AIRequest CreateRequest(string model = "zebracat/script-generator")
        => new()
        {
            ProviderId = "zebracat",
            Model = model,
            Input = new AIInput
            {
                Text = "Fallback idea",
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content = [new AITextContentPart { Type = "text", Text = "Old idea" }]
                    },
                    new AIInputItem
                    {
                        Role = "assistant",
                        Content = [new AITextContentPart { Type = "text", Text = "Old answer" }]
                    },
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "Latest idea part one" },
                            new AITextContentPart { Type = "text", Text = "Latest idea part two" }
                        ]
                    }
                ]
            },
            Metadata = new Dictionary<string, object?>
            {
                ["zebracat"] = new
                {
                    idea = "must be overwritten",
                    duration = 60,
                    language = "spanish",
                    mood = "inspiring",
                    prompt_style = "educational",
                    future_option = "preserved"
                }
            }
        };

    private static ZebracatProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        return new ZebracatProvider(new StaticApiKeyResolver(), cache, new StaticHttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

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
