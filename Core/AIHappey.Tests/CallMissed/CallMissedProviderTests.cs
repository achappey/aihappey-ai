using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.CallMissed;
using AIHappey.Messages;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AIHappey.Tests.CallMissed;

public sealed class CallMissedProviderTests
{
    [Fact]
    public void GetIdentifier_returns_callmissed()
    {
        var provider = CreateProvider(_ => JsonResponse("{}"));
        Assert.Equal("callmissed", provider.GetIdentifier());
    }

    [Fact]
    public async Task CompleteChatAsync_posts_to_chat_completions()
    {
        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/chat/completions")
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("gpt-4.1", doc.RootElement.GetProperty("model").GetString());

                return JsonResponse(
                    """
                    {
                      "id": "chatcmpl-1",
                      "object": "chat.completion",
                      "created": 1,
                      "model": "gpt-4.1",
                      "choices": [
                        {
                          "index": 0,
                          "message": { "role": "assistant", "content": "ok" },
                          "finish_reason": "stop"
                        }
                      ],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "gpt-4.1",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("hello", JsonSerializerOptions.Web)
                }
            ]
        });

        Assert.Equal("callmissed/gpt-4.1", response.Model);
    }

    [Fact]
    public async Task MessagesAsync_posts_to_messages_endpoint()
    {
        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/messages")
            {
                var body = await request.Content!.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                Assert.Equal("callmissed/claude-3.5-sonnet", doc.RootElement.GetProperty("model").GetString());

                return JsonResponse(
                    """
                    {
                      "id":"msg_1",
                      "type":"message",
                      "role":"assistant",
                      "model":"callmissed/claude-3.5-sonnet",
                      "content":[{"type":"text","text":"hello"}],
                      "stop_reason":"end_turn",
                      "usage":{"input_tokens":1,"output_tokens":1}
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.MessagesAsync(
            new MessagesRequest
            {
                Model = "callmissed/claude-3.5-sonnet",
                MaxTokens = 32,
                Messages =
                [
                    new MessageParam
                    {
                        Role = "user",
                        Content = new MessagesContent("hello")
                    }
                ]
            },
            []);

        Assert.Equal("msg_1", response.Id);
    }

    [Fact]
    public async Task SpeechRequest_posts_to_audio_speech_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-binary"))
                };

                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                return resp;
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "callmissed/tts-1",
            Text = "hello world"
        });

        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.NotEmpty(response.Audio.Base64);
    }

    [Fact]
    public async Task TranscriptionRequest_posts_to_audio_transcriptions_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/transcriptions")
            {
                return JsonResponse(
                    """
                    {
                      "text":"hello there",
                      "language":"en",
                      "duration":1.2,
                      "segments":[{"text":"hello there","start":0,"end":1.2}]
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "callmissed/whisper-1",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/mpeg"
        });

        Assert.Equal("hello there", response.Text);
        Assert.Equal("en", response.Language);
    }

    [Fact]
    public async Task ImageRequest_posts_to_images_generations_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/images/generations")
            {
                return JsonResponse(
                    """
                    {
                      "data": [
                        { "b64_json": "aGVsbG8=" }
                      ]
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "callmissed/gpt-image-1",
            Prompt = "draw"
        });

        Assert.Single(response.Images!);
    }

    [Fact]
    public async Task GetRealtimeToken_maps_livekit_token_and_ws_url()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/voice/sessions")
            {
                return JsonResponse(
                    """
                    {
                      "token":"livekit-token-123",
                      "expires_at": 1910000000,
                      "ws_url":"wss://voice.callmissed.com/session"
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "callmissed/realtime-1"
        });

        Assert.Equal("livekit-token-123", response.Value);
        Assert.Equal(1910000000, response.ExpiresAt);
        Assert.True(response.ProviderMetadata?.TryGetValue("callmissed", out var metadata) == true);
    }

    [Fact]
    public async Task ListModels_maps_model_fields()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse(
                    """
                    {
                      "data": [
                        {
                          "id": "gpt-4.1",
                          "name": "GPT-4.1",
                          "description": "Language model",
                          "owned_by": "openai",
                          "type": "language",
                          "context_length": 128000,
                          "pricing": { "input": "0.00001", "output": "0.00003" }
                        }
                      ]
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Single(models);
        Assert.Equal("callmissed/gpt-4.1", models[0].Id);
        Assert.Equal("language", models[0].Type);
        Assert.Equal(128000, models[0].ContextWindow);
        Assert.NotNull(models[0].Pricing);
    }

    private static CallMissedProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));

        return new CallMissedProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private static CallMissedProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "callmissed" ? "test-key" : null;
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
