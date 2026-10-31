using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Volink;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Volink;

public sealed class VolinkProviderTests
{
    [Fact]
    public async Task ListModels_maps_live_metadata_and_normalizes_decision_type()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api.volink.org/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "data": [
                    {
                      "id": "chat-model",
                      "name": "Chat model",
                      "owned_by": "Volink",
                      "created": 123,
                      "description": "Chat endpoint model",
                      "context_window": 4096,
                      "max_tokens": 2048,
                      "tags": ["chat"]
                    },
                    {
                      "id": "decision-model",
                      "type": "decisions",
                      "owned_by": "Volink"
                    }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(2, models.Length);
        Assert.Equal("volink/chat-model", models[0].Id);
        Assert.Equal("Chat model", models[0].Name);
        Assert.Equal("Volink", models[0].OwnedBy);
        Assert.Equal(123L, models[0].Created);
        Assert.Equal("Chat endpoint model", models[0].Description);
        Assert.Equal(4096, models[0].ContextWindow);
        Assert.Equal(2048, models[0].MaxTokens);
        Assert.Equal(["chat"], models[0].Tags);
        Assert.Equal("volink/decision-model", models[1].Id);
        Assert.Equal("decision", models[1].Type);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Chat_completions_uses_documented_endpoint_and_bearer_auth()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.volink.org/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return JsonResponse("""
                {
                  "id": "chat-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "chat-model",
                  "choices": [
                    {
                      "index": 0,
                      "message": { "role": "assistant", "content": "hello" },
                      "finish_reason": "stop"
                    }
                  ],
                  "usage": {
                    "prompt_tokens": 2,
                    "completion_tokens": 1,
                    "total_tokens": 3
                  }
                }
                """);
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "chat-model"
        });

        var json = JsonSerializer.SerializeToElement(response);
        Assert.Equal("hello", json.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString());
        Assert.Equal(3, json.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Responses_and_anthropic_messages_use_their_documented_endpoints()
    {
        var paths = new List<string>();
        var provider = CreateProvider(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath == "/v1/responses")
                return JsonResponse("""
                    {"id":"resp_1","object":"response","created_at":1,"model":"chat-model",
                     "status":"completed","output":[],
                     "usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}
                    """);

            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return JsonResponse("""
                {"id":"msg_1","type":"message","role":"assistant","model":"chat-model",
                 "content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn",
                 "usage":{"input_tokens":2,"output_tokens":1}}
                """);
        });

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "chat-model",
            Input = "hello"
        });
        var message = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "chat-model",
            MaxTokens = 10,
            Messages = []
        }, new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" });

        Assert.Equal("completed", response.Status);
        Assert.Equal("hello", message.Content.Single().Text);
        Assert.Equal(["/v1/responses", "/v1/messages"], paths);
    }

    [Fact]
    public async Task Image_generation_and_edit_use_documented_openai_routes()
    {
        var paths = new List<string>();
        var provider = CreateProvider(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return JsonResponse("""{"created":1,"data":[{"b64_json":"AQ=="}],"output_format":"png"}""");
        });

        var generation = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "image-model",
            Prompt = "a blue circle"
        });
        var edit = await provider.OpenAIImageEditRequestAsync(new OpenAIImageEditRequest
        {
            Model = "image-model",
            Prompt = "make it green",
            Images = [new OpenAIImageReference { ImageUrl = "data:image/png;base64,AQ==" }]
        });
        var unifiedEdit = await provider.ImageRequest(new ImageRequest
        {
            Model = "image-model",
            Prompt = "make it green",
            Files = [new ImageFile { MediaType = "image/png", Data = "AQ==" }]
        });

        Assert.Single(generation.Data!);
        Assert.Single(edit.Data!);
        Assert.Single(unifiedEdit.Images!);
        Assert.Equal([
            "/v1/images/generations",
            "/v1/images/edits",
            "/v1/images/edits"
        ], paths);
    }

    [Fact]
    public async Task Speech_uses_documented_audio_speech_endpoint()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.volink.org/v1/audio/speech", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("speech-model", body.RootElement.GetProperty("model").GetString());
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new("audio/mpeg");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };
        });

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new()
        {
            Model = "speech-model",
            Input = "hello",
            Voice = "alloy"
        });

        Assert.Equal(new byte[] { 1, 2, 3 }, audio);
        Assert.Equal("audio/mpeg", mimeType);
    }

    [Fact]
    public async Task Decision_request_uses_system_one_and_maps_boolean_answer()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("https://api.volink.org/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("decision-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("flag")
                .GetProperty("type").GetString());
            return JsonResponse("""
                {
                  "model": "decision-model",
                  "answers": { "flag": { "type": "noul", "noul": 0.8 } },
                  "usage": { "input_tokens": 2, "output_tokens": 1 }
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "volink/decision-model",
            State = JsonSerializer.SerializeToElement(new { signal = "yes" }),
            Questions = new()
            {
                ["flag"] = new DecisionBooleanQuestion { Instructions = "Is the signal present?" }
            }
        });

        Assert.Equal(0.8, Assert.IsType<DecisionBooleanAnswer>(result.Answers["flag"]).Probability);
        Assert.Equal(2, result.Usage!.InputTokens);
        Assert.Equal("volink/decision-model", result.Response!.ModelId);
    }

    [Fact]
    public async Task Undocumented_capabilities_fail_explicitly_without_sending_requests()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Unexpected HTTP request"));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.RerankingRequest(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIEmbeddingRequestAsync(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscriptionRequest(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.StartVideoOperation(new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeTokenAsync("model", new()));
    }

    private static VolinkProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider((request, _) => Task.FromResult(responder(request)));

    private static VolinkProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
