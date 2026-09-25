using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Baseten;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Baseten;

public sealed class BasetenProviderAudioInputTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_maps_user_audio_to_Baseten_audio_url_shape()
    {
        JsonElement? payload = null;
        var provider = CreateProvider(async request =>
        {
            payload = await ReadPayloadAsync(request);
            return JsonResponse("""
                {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"thinkingmachines/inkling","choices":[{"index":0,"message":{"role":"assistant","content":"Done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                """);
        });

        await provider.ExecuteUnifiedAsync(CreateRequest(
            new AITextContentPart { Type = "text", Text = "Summarize the recordings." },
            Audio("audio/wav", "https://cdn.example.test/audio.wav"),
            Audio("audio/wav", "data:audio/wav;base64,ZXhpc3Rpbmc="),
            Audio("audio/mpeg", "cmF3LW1wMw=="),
            new AIFileContentPart
            {
                Type = "file",
                MediaType = "image/png",
                Data = "https://cdn.example.test/image.png"
            }));

        var content = Assert.Single(payload!.Value.GetProperty("messages").EnumerateArray()).GetProperty("content");
        Assert.Equal(5, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Summarize the recordings.", content[0].GetProperty("text").GetString());
        AssertAudioUrl(content[1], "https://cdn.example.test/audio.wav");
        AssertAudioUrl(content[2], "data:audio/wav;base64,ZXhpc3Rpbmc=");
        AssertAudioUrl(content[3], "data:audio/mpeg;base64,cmF3LW1wMw==");
        Assert.Equal("image_url", content[4].GetProperty("type").GetString());
        Assert.Equal("https://cdn.example.test/image.png", content[4].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task StreamUnifiedAsync_maps_user_audio_to_Baseten_audio_url_shape()
    {
        JsonElement? payload = null;
        var provider = CreateProvider(async request =>
        {
            payload = await ReadPayloadAsync(request);
            return SseResponse("""
                data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1,"model":"thinkingmachines/inkling-small","choices":[{"index":0,"delta":{"role":"assistant","content":"Done"},"finish_reason":null}]}

                data: {"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1,"model":"thinkingmachines/inkling-small","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """);
        });

        await foreach (var _ in provider.StreamUnifiedAsync(CreateRequest(Audio("audio/wav", "c3RyZWFtLWF1ZGlv"))))
        {
        }

        Assert.True(payload!.Value.GetProperty("stream").GetBoolean());
        var content = Assert.Single(payload.Value.GetProperty("messages").EnumerateArray()).GetProperty("content");
        AssertAudioUrl(Assert.Single(content.EnumerateArray()), "data:audio/wav;base64,c3RyZWFtLWF1ZGlv");
    }

    private static AIRequest CreateRequest(params AIContentPart[] parts)
        => new()
        {
            ProviderId = "baseten",
            Model = "thinkingmachines/inkling",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content = [.. parts]
                    }
                ]
            }
        };

    private static AIFileContentPart Audio(string mediaType, string data)
        => new()
        {
            Type = "file",
            MediaType = mediaType,
            Data = data
        };

    private static void AssertAudioUrl(JsonElement part, string expectedUrl)
    {
        Assert.Equal("audio_url", part.GetProperty("type").GetString());
        Assert.Equal(expectedUrl, part.GetProperty("audio_url").GetProperty("url").GetString());
        Assert.False(part.TryGetProperty("input_audio", out _));
    }

    private static BasetenProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static async Task<JsonElement> ReadPayloadAsync(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/chat/completions", request.RequestUri?.AbsolutePath);
        var body = await request.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
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

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await responder(request);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
