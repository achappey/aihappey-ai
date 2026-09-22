using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Mireye;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Mireye;

public sealed class MireyeProviderTests
{
    [Fact]
    public async Task ListModels_exposes_ask_slug()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No HTTP request expected."));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("mireye/ask", model.Id);
        Assert.Equal("language", model.Type);
        Assert.Contains("streaming", model.Tags ?? []);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_posts_address_question_and_maps_provenance()
    {
        JsonElement submitted = default;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/ask", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            submitted = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse(FinalResponse("Address answer"));
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest(
            new { address = "350 5th Ave, New York, NY 10118", include_trace = true },
            items:
            [
                User("Older question"),
                new AIInputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "Older answer" }] },
                User("Is this in a flood zone?")
            ]));

        Assert.Equal("350 5th Ave, New York, NY 10118", submitted.GetProperty("address").GetString());
        Assert.False(submitted.TryGetProperty("lat", out _));
        Assert.Equal("Is this in a flood zone?", submitted.GetProperty("question").GetString());
        Assert.True(submitted.GetProperty("include_trace").GetBoolean());
        Assert.Equal("mireye/ask", response.Model);
        Assert.Equal("completed", response.Status);
        Assert.Null(response.Usage);

        var items = response.Output?.Items ?? [];
        var message = Assert.Single(items, item => item.Type == "message");
        Assert.Equal("Address answer", Assert.IsType<AITextContentPart>(Assert.Single(message.Content ?? [])).Text);
        var source = Assert.Single(items, item => item.Type == "source-url");
        Assert.Equal("https://example.test/source", source.Metadata?["chatcompletions.source.url"]);
        Assert.NotNull(response.Metadata?["mireye.resolved_location"]);
        Assert.NotNull(response.Metadata?["mireye.data_gaps"]);
        Assert.NotNull(response.Metadata?["mireye.raw"]);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_posts_coordinate_pair()
    {
        JsonElement submitted = default;
        var provider = CreateProvider(request =>
        {
            submitted = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse(FinalResponse("Coordinate answer"));
        });

        await provider.ExecuteUnifiedAsync(CreateRequest(new { lat = 46.6, lng = -93.7 }));

        Assert.Equal(46.6, submitted.GetProperty("lat").GetDouble());
        Assert.Equal(-93.7, submitted.GetProperty("lng").GetDouble());
        Assert.False(submitted.TryGetProperty("address", out _));
        Assert.False(submitted.GetProperty("include_trace").GetBoolean());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("latitude_alias")]
    [InlineData("partial")]
    [InlineData("both")]
    [InlineData("bad_lat")]
    public async Task ExecuteUnifiedAsync_rejects_invalid_location_before_http(string scenario)
    {
        var called = false;
        var provider = CreateProvider(_ =>
        {
            called = true;
            return JsonResponse(FinalResponse("not reached"));
        });
        object metadata = scenario switch
        {
            "latitude_alias" => new { latitude = 46.6, longitude = -93.7 },
            "partial" => new { lat = 46.6 },
            "both" => new { address = "Aitkin County, MN", lat = 46.6, lng = -93.7 },
            "bad_lat" => new { lat = 100.0, lng = -93.7 },
            _ => new { include_trace = false }
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest(metadata)));

        Assert.False(called);
    }

    [Fact]
    public async Task StreamUnifiedAsync_maps_deltas_final_and_citations()
    {
        var final = FinalResponse("Hello world");
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/ask/stream", request.RequestUri?.AbsolutePath);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "text/event-stream");
            return SseResponse(
                "event: delta\ndata: {\"text\":\"Hello \"}\n\n" +
                "event: delta\ndata: {\"text\":\"world\"}\n\n" +
                $"event: final\ndata: {final}\n\n");
        });

        var events = await FixtureAssertions.CollectAsync(
            provider.StreamUnifiedAsync(CreateRequest(new { lat = 46.6, lng = -93.7 })));

        Assert.Equal(
            ["text-start", "text-delta", "text-delta", "text-end", "source-url", "data-mireye.final", "finish"],
            events.Select(value => value.Event.Type));
        Assert.Equal("Hello ", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
        Assert.Equal("world", Assert.IsType<AITextDeltaEventData>(events[2].Event.Data).Delta);
        var source = Assert.IsType<AISourceUrlEventData>(events[4].Event.Data);
        Assert.Equal("https://example.test/source", source.Url);
        Assert.Equal("mireye/ask", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).Model);
    }

    [Fact]
    public async Task StreamUnifiedAsync_terminal_error_discards_partial_answer_semantically()
    {
        var provider = CreateProvider(_ => SseResponse(
            "event: delta\ndata: {\"text\":\"partial\"}\n\n" +
            "event: error\ndata: {\"error\":\"ask_answer_incomplete\",\"message\":\"cut off\",\"retryable\":true}\n\n"));

        var events = await FixtureAssertions.CollectAsync(
            provider.StreamUnifiedAsync(CreateRequest(new { address = "Dallas, TX" })));

        Assert.Equal(["text-start", "text-delta", "text-end", "error"], events.Select(value => value.Event.Type));
        Assert.Equal("cut off", Assert.IsType<AIErrorEventData>(events[^1].Event.Data).ErrorText);
        Assert.DoesNotContain(events, value => value.Event.Type == "finish");
    }

    [Fact]
    public async Task StreamUnifiedAsync_reports_missing_final_frame()
    {
        var provider = CreateProvider(_ => SseResponse("event: delta\ndata: {\"text\":\"partial\"}\n\n"));

        var events = await FixtureAssertions.CollectAsync(
            provider.StreamUnifiedAsync(CreateRequest(new { address = "Dallas, TX" })));

        Assert.Equal("error", events[^1].Event.Type);
        Assert.Contains("without its authoritative final frame", Assert.IsType<AIErrorEventData>(events[^1].Event.Data).ErrorText);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_surfaces_status_retry_after_and_structured_body()
    {
        var provider = CreateProvider(_ =>
        {
            var response = JsonResponse("{\"detail\":{\"error\":\"ask_busy\",\"message\":\"busy\",\"retryable\":true}}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return response;
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.ExecuteUnifiedAsync(CreateRequest(new { address = "Dallas, TX" })));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("Retry-After: 5", exception.Message);
        Assert.Contains("ask_busy", exception.Message);
    }

    private static AIRequest CreateRequest(object mireyeMetadata, List<AIInputItem>? items = null)
        => new()
        {
            ProviderId = "mireye",
            Model = "mireye/ask",
            Input = new AIInput
            {
                Text = "What is the wildfire risk?",
                Items = items
            },
            Metadata = new Dictionary<string, object?>
            {
                ["mireye"] = JsonSerializer.SerializeToElement(mireyeMetadata, JsonSerializerOptions.Web)
            }
        };

    private static AIInputItem User(string text)
        => new()
        {
            Role = "user",
            Content = [new AITextContentPart { Type = "text", Text = text }]
        };

    private static string FinalResponse(string answer)
        => JsonSerializer.Serialize(new
        {
            lat = 46.6,
            lng = -93.7,
            question = "What is the risk?",
            answered_at = "2026-06-12T07:31:33Z",
            answer,
            confidence = "high",
            citations = new[]
            {
                new
                {
                    source = "USGS",
                    source_url = "https://example.test/source",
                    fields = new[] { "elevation" },
                    fetched_at = "2026-06-12T07:31:30Z",
                    confidence = "high"
                }
            },
            fields_used = new[] { "elevation" },
            data_gaps = Array.Empty<object>(),
            resolved_location = new { lat = 46.6, lng = -93.7, source = "coordinate" },
            trace = new { planner_model = "claude-haiku-4-5" }
        }, JsonSerializerOptions.Web);

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpResponseMessage SseResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private static MireyeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
            => Task.FromResult(responder(request));
    }
}
