using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Lightcone;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Lightcone;

public sealed class LightconeProviderTests
{
    [Fact]
    public async Task ListModels_discovers_native_model_ids_and_caches_results()
    {
        var calls = 0;
        var provider = CreateProvider(request =>
        {
            calls++;
            Assert.Equal("https://api.tzafon.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "tzafon.northstar-cua-fast-1.6",
                      "object": "model",
                      "owned_by": "Tzafon",
                      "custom": {
                        "nested": true
                      }
                    }
                  ]
                }
                """);
        });

        var models = (await provider.ListModels()).ToArray();

        Assert.Single(models);
        Assert.Equal("lightcone/tzafon.northstar-cua-fast-1.6", models[0].Id);
        Assert.Equal("tzafon.northstar-cua-fast-1.6", models[0].Name);
        Assert.Equal("Tzafon", models[0].OwnedBy);
        Assert.Equal("language", models[0].Type);
        Assert.Single(await provider.ListModels());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListModels_uses_official_static_catalog_without_an_api_key()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException(), apiKey: null);

        var models = (await provider.ListModels()).ToArray();

        Assert.Equal(
            [
                "lightcone/tzafon.northstar-cua-fast-1.6",
                "lightcone/tzafon.northstar-cua-fast",
                "lightcone/tzafon.sm-1"
            ],
            models.Select(model => model.Id));
    }

    [Fact]
    public async Task CompleteChat_uses_openai_compatible_endpoint_and_preserves_native_options_safely()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.tzafon.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("trace-1", request.Headers.GetValues("X-Trace-Id").Single());
            Assert.False(request.Headers.Contains("X-Lightcone-Key"));

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("tzafon.sm-1", body.RootElement.GetProperty("model").GetString());
            Assert.True(body.RootElement.GetProperty("native_option").GetProperty("nested").GetBoolean());

            return JsonResponse("""
                {
                  "id": "chatcmpl-1",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "tzafon.sm-1",
                  "choices": [],
                  "usage": {
                    "prompt_tokens": 2,
                    "completion_tokens": 3,
                    "total_tokens": 5
                  }
                }
                """);
        });

        var completion = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "tzafon.sm-1",
            Headers = new()
            {
                ["Authorization"] = "Bearer untrusted",
                ["X-Lightcone-Key"] = "untrusted",
                ["X-Trace-Id"] = "trace-1"
            },
            Metadata = new()
            {
                ["lightcone"] = JsonSerializer.SerializeToElement(new
                {
                    native_option = new { nested = true }
                })
            }
        });

        Assert.Equal("lightcone/tzafon.sm-1", completion.Model);
        var completionJson = JsonSerializer.SerializeToElement(completion);
        Assert.Equal(5, completionJson.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task ExecuteUnified_uses_chat_completions_for_standardized_conversation()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.tzafon.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("tzafon.sm-1", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());

            return JsonResponse("""
                {
                  "id": "chatcmpl-unified",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "tzafon.sm-1",
                  "choices": [
                    {
                      "index": 0,
                      "message": {
                        "role": "assistant",
                        "content": "hello"
                      },
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

        var request = new ChatCompletionOptions
        {
            Model = "tzafon.sm-1",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("hello")
                }
            ]
        }.ToUnifiedRequest("lightcone");

        var response = await provider.ExecuteUnifiedAsync(request);

        Assert.NotNull(response);
    }

    [Fact]
    public async Task Messages_uses_shared_chat_translation()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.tzafon.ai/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("tzafon.sm-1", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());

            return JsonResponse("""
                {
                  "id": "chatcmpl-messages",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "tzafon.sm-1",
                  "choices": [
                    {
                      "index": 0,
                      "message": {
                        "role": "assistant",
                        "content": "hello"
                      },
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

        var request = JsonSerializer.Deserialize<global::AIHappey.Messages.MessagesRequest>("""
            {
              "model": "tzafon.sm-1",
              "max_tokens": 20,
              "messages": [
                {
                  "role": "user",
                  "content": "hello"
                }
              ]
            }
            """)!;

        var response = await provider.MessagesAsync(request, []);

        Assert.Equal("lightcone/tzafon.sm-1", response.Model);
        Assert.Equal("hello", Assert.Single(response.Content).Text);
    }

    [Fact]
    public async Task Responses_preserves_native_fields_with_gateway_storage_policy()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("https://api.tzafon.ai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.False(request.Headers.Contains("X-Lightcone-Key"));

            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("tzafon.northstar-cua-fast-1.6", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("resp_previous", body.RootElement.GetProperty("previous_response_id").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            var tool = body.RootElement.GetProperty("tools")[0];
            Assert.Equal("computer_use", tool.GetProperty("type").GetString());
            Assert.Equal(1280, tool.GetProperty("display_width").GetInt32());

            return JsonResponse("""
                {
                  "id": "resp_current",
                  "object": "response",
                  "created_at": 1,
                  "status": "completed",
                  "model": "tzafon.northstar-cua-fast-1.6",
                  "output": [
                    {
                      "type": "computer_call",
                      "call_id": "call_1",
                      "action": {
                        "type": "click",
                        "x": 450,
                        "y": 320
                      }
                    }
                  ],
                  "usage": {
                    "input_tokens": 4,
                    "output_tokens": 2,
                    "total_tokens": 6
                  }
                }
                """);
        });

        var result = await provider.ResponsesAsync(new global::AIHappey.Responses.ResponseRequest
        {
            Model = "lightcone/tzafon.northstar-cua-fast-1.6",
            Input = "Continue",
            Store = true,
            AdditionalProperties = new()
            {
                ["previous_response_id"] = JsonSerializer.SerializeToElement("resp_previous")
            },
            Tools =
            [
                new global::AIHappey.Responses.ResponseToolDefinition
                {
                    Type = "computer_use",
                    Extra = new()
                    {
                        ["display_width"] = JsonSerializer.SerializeToElement(1280),
                        ["display_height"] = JsonSerializer.SerializeToElement(720),
                        ["environment"] = JsonSerializer.SerializeToElement("browser")
                    }
                }
            ],
            Headers = new()
            {
                ["Authorization"] = "Bearer untrusted",
                ["X-Lightcone-Key"] = "untrusted"
            }
        });

        Assert.Equal("resp_current", result.Id);
        Assert.Equal("lightcone/tzafon.northstar-cua-fast-1.6", result.Model);
        Assert.Equal(6, result.NormalizedUsage!.TotalTokens);
        var output = Assert.IsType<JsonElement>(Assert.Single(result.Output));
        Assert.Equal("computer_call", output.GetProperty("type").GetString());
        Assert.Equal("click", output.GetProperty("action").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Streaming_chat_and_responses_emit_provider_events()
    {
        var requests = 0;
        var provider = CreateProvider(request =>
        {
            requests++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                return EventResponse("""
                    data: {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"tzafon.sm-1","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

                    data: [DONE]

                    """);
            }

            return EventResponse("""
                data: {"type":"response.output_text.delta","delta":"hello"}

                data: [DONE]

                """);
        });

        var chatUpdates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "tzafon.sm-1",
            Stream = true
        }))
        {
            chatUpdates.Add(update);
        }

        var responseParts = new List<global::AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new global::AIHappey.Responses.ResponseRequest
        {
            Model = "tzafon.sm-1",
            Input = "hello"
        }))
        {
            responseParts.Add(part);
        }

        Assert.Single(chatUpdates);
        Assert.Equal("lightcone/tzafon.sm-1", chatUpdates[0].Model);
        Assert.Single(responseParts);
        Assert.Equal("response.output_text.delta", responseParts[0].Type);
        Assert.Equal(2, requests);
    }

    private static LightconeProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey = "test-key")
        => new(
            new StaticApiKeyResolver(apiKey),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))
            {
                BaseAddress = new Uri("https://api.tzafon.ai/")
            }),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage EventResponse(string events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
        };

    private sealed class StaticApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
