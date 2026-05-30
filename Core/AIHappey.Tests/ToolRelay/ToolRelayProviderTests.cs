using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ToolRelay;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ToolRelay;

public sealed class ToolRelayProviderTests
{
    [Fact]
    public void GetIdentifier_returns_toolrelay()
    {
        var provider = CreateProvider(_ => JsonResponse("{}"));
        Assert.Equal("toolrelay", provider.GetIdentifier());
    }

    [Fact]
    public async Task ListModels_maps_chat_tts_stt_image_video_types()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri?.AbsolutePath);

            return JsonResponse(
                """
                {
                  "total": 5,
                  "models": [
                    { "id": "openrouter/deepseek/deepseek-v3.2", "vendor": "openrouter", "name": "DeepSeek", "type": "chat", "description": "chat model" },
                    { "id": "deepgram/nova-2", "vendor": "deepgram", "name": "Nova 2", "type": "stt", "description": "speech to text" },
                    { "id": "elevenlabs/multilingual-v2", "vendor": "elevenlabs", "name": "Multilingual v2", "type": "tts", "description": "text to speech" },
                    { "id": "replicate/black-forest-labs/flux-2-pro", "vendor": "replicate", "name": "Flux 2 Pro", "type": "image", "description": "image model" },
                    { "id": "seedance/seedance-2.0", "vendor": "seedance", "name": "Seedance 2.0", "type": "video", "description": "video model" }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains(models, m => m.Id == "toolrelay/openrouter/deepseek/deepseek-v3.2" && m.Type == "language");
        Assert.Contains(models, m => m.Id == "toolrelay/deepgram/nova-2" && m.Type == "transcription");
        Assert.Contains(models, m => m.Id == "toolrelay/elevenlabs/multilingual-v2" && m.Type == "speech");
        Assert.Contains(models, m => m.Id == "toolrelay/replicate/black-forest-labs/flux-2-pro" && m.Type == "image");
        Assert.Contains(models, m => m.Id == "toolrelay/seedance/seedance-2.0" && m.Type == "video");
    }

    [Fact]
    public async Task CompleteChatAsync_posts_to_chat_completions()
    {
        JsonDocument? capturedBody = null;

        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                capturedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                return JsonResponse(
                    """
                    {
                      "id": "chatcmpl-1",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "openrouter/deepseek/deepseek-v3.2",
                      "choices": [
                        {
                          "index": 0,
                          "message": { "role": "assistant", "content": "Hello from ToolRelay" },
                          "finish_reason": "stop"
                        }
                      ],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 2, "total_tokens": 3 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "toolrelay/openrouter/deepseek/deepseek-v3.2",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hello", JsonSerializerOptions.Web)
                }
            ]
        });

        Assert.NotNull(capturedBody);
        Assert.Equal("toolrelay/openrouter/deepseek/deepseek-v3.2", capturedBody!.RootElement.GetProperty("model").GetString());
        Assert.Equal("toolrelay/openrouter/deepseek/deepseek-v3.2", response.Model);
    }

    [Fact]
    public async Task Non_chat_media_methods_throw_not_supported()
    {
        var provider = CreateProvider(_ => JsonResponse("{}"));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "toolrelay/replicate/black-forest-labs/flux-2-pro",
            Prompt = "test"
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "toolrelay/elevenlabs/multilingual-v2",
            Text = "test"
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "toolrelay/deepgram/nova-2",
            Audio = "dGVzdA==",
            MediaType = "audio/wav"
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.VideoRequest(new VideoRequest
        {
            Model = "toolrelay/seedance/seedance-2.0",
            Prompt = "test"
        }));
    }

    private static ToolRelayProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new ToolRelayProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private static ToolRelayProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "toolrelay" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
