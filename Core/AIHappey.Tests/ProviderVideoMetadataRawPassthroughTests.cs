using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.BytePlus;
using AIHappey.Core.Providers.FreedomGPT;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderVideoMetadataRawPassthroughTests
{
    [Fact]
    public async Task Byteplus_video_options_preserve_unknown_json_and_map_known_image_roles()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/api/v3/contents/generations/tasks", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(HttpStatusCode.OK, """{"id":"task-1"}""");
        });
        var metadata = ParseElement("""
            {
              "model": "wrong-model",
              "content": "wrong-content",
              "generate_audio": true,
              "watermark": false,
              "camera_fixed": null,
              "image_roles": {
                "first_frame": "data:image/png;base64,AQID",
                "reference_images": ["data:image/jpeg;base64,BAUG"],
                "future_nested": {"items": [true, null, {"value": 2}]}
              },
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new VideoRequest
        {
            Model = "byteplus/seedance-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["byteplus"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateBytePlusProvider(client).StartVideoOperation(request);

        Assert.Equal("seedance-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(payload.GetProperty("generate_audio").GetBoolean());
        Assert.False(payload.GetProperty("watermark").GetBoolean());
        Assert.False(payload.TryGetProperty("camera_fixed", out _));
        Assert.Equal("first_frame", payload.GetProperty("content")[1].GetProperty("role").GetString());
        Assert.Equal("reference_image", payload.GetProperty("content")[2].GetProperty("role").GetString());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("image_roles").GetProperty("future_nested"),
            payload.GetProperty("image_roles").GetProperty("future_nested")));

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("byteplus", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Byteplus_video_options_keep_validation_and_upstream_error_behavior()
    {
        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = BytePlusRequest("""{"generate_audio":42}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateBytePlusProvider(invalidClient).StartVideoOperation(request));

        request = BytePlusRequest("""{"image_roles":{"first_frame":42}}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateBytePlusProvider(invalidClient).StartVideoOperation(request));

        using var failedClient = CreateClient(_ =>
            JsonResponse(HttpStatusCode.BadRequest, "upstream failure"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateBytePlusProvider(failedClient).StartVideoOperation(BytePlusRequest("""{"future":true}""")));
        Assert.Equal("BytePlus video create failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task Freedomgpt_video_options_forward_unknown_json_and_keep_resolved_fields()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/api/v1/actor-gpt/create", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(HttpStatusCode.OK, """{"videoId":"video-1"}""");
        });
        var metadata = ParseElement("""
            {
              "actorId": "actor-1",
              "voiceId": "voice-1",
              "script": "metadata script",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new VideoRequest
        {
            Model = "freedomgpt/actor-gpt",
            Prompt = "Request script",
            ProviderOptions = new()
            {
                ["freedomgpt"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateFreedomGPTProvider(client).StartVideoOperation(request);

        Assert.Equal("actor-1", payload.GetProperty("actorId").GetString());
        Assert.Equal("voice-1", payload.GetProperty("voiceId").GetString());
        Assert.Equal("Request script", payload.GetProperty("script").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("freedomgpt", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Freedomgpt_video_options_preserve_required_fields_validation_and_errors()
    {
        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var provider = CreateFreedomGPTProvider(unusedClient);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.StartVideoOperation(new VideoRequest
        {
            Model = "freedomgpt/actor-gpt",
            Prompt = "Missing provider options"
        }));

        var invalidRequest = new VideoRequest
        {
            Model = "freedomgpt/actor-gpt",
            Prompt = "Invalid actor",
            ProviderOptions = new()
            {
                ["freedomgpt"] = ParseElement("""{"actorId":42,"voiceId":"voice-1"}""")
            }
        };
        await Assert.ThrowsAsync<JsonException>(() => provider.StartVideoOperation(invalidRequest));

        using var failedClient = CreateClient(_ => JsonResponse(HttpStatusCode.BadRequest, "upstream failure"));
        var failedProvider = CreateFreedomGPTProvider(failedClient);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failedProvider.StartVideoOperation(FreedomGPTRequest("""{"actorId":"actor-1","voiceId":"voice-1"}""")));
        Assert.Equal("FreedomGPT ActorGPT create failed (400): upstream failure", error.Message);
    }

    private static VideoRequest BytePlusRequest(string metadata)
        => new()
        {
            Model = "byteplus/seedance-model",
            Prompt = "A landscape",
            ProviderOptions = new() { ["byteplus"] = ParseElement(metadata) }
        };

    private static VideoRequest FreedomGPTRequest(string metadata)
        => new()
        {
            Model = "freedomgpt/actor-gpt",
            Prompt = "A landscape",
            ProviderOptions = new() { ["freedomgpt"] = ParseElement(metadata) }
        };

    private static BytePlusProvider CreateBytePlusProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static FreedomGPTProvider CreateFreedomGPTProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
        => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
