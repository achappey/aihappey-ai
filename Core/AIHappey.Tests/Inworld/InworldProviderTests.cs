using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Inworld;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Inworld;

public sealed class InworldProviderTests
{
    [Fact]
    public async Task ListModels_returns_api_models_catalog_entries_and_router_shortcuts()
    {
        var requestedPaths = new List<string>();
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            requestedPaths.Add(path);

            if (path.StartsWith("/llm/v1alpha/models", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "models": [
                        {
                          "model": "gpt-4o",
                          "provider": "openai",
                          "modelCreator": "OpenAI",
                          "pricing": {
                            "prompt": 0.0000025,
                            "completion": 0.00001
                          },
                          "spec": {
                            "inputModalities": ["text", "image"],
                            "outputModalities": ["text"],
                            "contextLength": 128000,
                            "maxCompletionTokens": 16384,
                            "supportedParameters": ["max_tokens", "tools"],
                            "capabilities": {
                              "functionCalling": true,
                              "vision": true,
                              "promptCaching": true
                            }
                          },
                          "isSupported": true
                        },
                        {
                          "model": "jev-latest",
                          "provider": "typesafe",
                          "modelCreator": "Typesafe",
                          "pricing": {
                            "prompt": 0.000001,
                            "completion": 0
                          },
                          "spec": {
                            "inputModalities": ["text"],
                            "outputModalities": ["decisions"]
                          },
                          "isSupported": true
                        }
                      ]
                    }
                    """);
            }

            // Listing also discovers standard and community speech-voice shortcuts.
            if (path.StartsWith("/voices/v1/voices", StringComparison.OrdinalIgnoreCase))
                return JsonResponse("""{"voices":[]} """);

            if (path.StartsWith("/router/v1/routers", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    {
                      "routers": [
                        {
                          "name": "my-router",
                          "displayName": "My Router",
                          "routes": [
                            {
                              "route": {
                                "route_id": "premium",
                                "variants": []
                              },
                              "condition": {
                                "cel_expression": "true"
                              }
                            }
                          ],
                          "defaultRoute": {
                            "route_id": "default",
                            "variants": []
                          }
                        }
                      ]
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var models = (await provider.ListModels()).ToList();

        var routedModel = Assert.Single(models, model => model.Id == "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o");
        Assert.Equal("gpt-4o", routedModel.Name);
        Assert.Equal("OpenAI", routedModel.OwnedBy);
        Assert.Equal(128000, routedModel.ContextWindow);
        Assert.Equal(16384, routedModel.MaxTokens);
        Assert.Equal(0.0000025m, routedModel.Pricing?.Input);
        Assert.Equal(0.00001m, routedModel.Pricing?.Output);
        Assert.Equal("language", routedModel.Type);
        var decisionModel = Assert.Single(models, model => model.Id == "inworld/typesafe/jev-latest");
        Assert.Equal("decision", decisionModel.Type);
        // API model capabilities and router policies are not classification tags.
        Assert.Empty(routedModel.Tags ?? []);

        Assert.Contains(requestedPaths, path => path.StartsWith("/voices/v1/voices?", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("filter=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requestedPaths, path => path.StartsWith("/voices/v1/voices?", StringComparison.OrdinalIgnoreCase)
            && Uri.UnescapeDataString(path).Contains("filter=community = \"true\"", StringComparison.Ordinal));
        Assert.Contains("/llm/v1alpha/models?output_modalities=decisions", requestedPaths);

        Assert.Contains(models, model => model.Id == "inworld/auto");

        var routerModel = Assert.Single(models, model => model.Id == "inworld/my-router");
        Assert.Equal("My Router", routerModel.Name);
        Assert.Equal("language", routerModel.Type);
        Assert.Equal("Inworld", routerModel.OwnedBy);
        Assert.Contains("my-router", routerModel.Description);
        Assert.Contains("conditional routes: 1", routerModel.Description);
        Assert.Contains("default route: yes", routerModel.Description);
        Assert.Empty(routerModel.Tags ?? []);
    }

    [Fact]
    public async Task Decisions_send_native_noul_choice_and_score_questions_and_map_usage()
    {
        JsonDocument? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.inworld.ai/alpha/decisions", request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            capturedBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return JsonResponse(
                """
                {
                  "model": "typesafe/jev-1.13.0",
                  "answers": {
                    "urgent": {"type":"noul","noul":0.95},
                    "team": {
                      "type":"choice","choice":"billing",
                      "probabilities":{"billing":0.8,"technical":0.2},"confidence":0.8
                    },
                    "mood": {
                      "type":"score","score":0.75,"legend":{"0":"calm","1":"upset"},
                      "probabilities":{"0":0.25,"1":0.75},"confidence":0.75
                    }
                  },
                  "usage":{"input_tokens":12,"output_tokens":5},
                  "metadata":{"generation_id":"generation-1"}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "inworld/typesafe/jev-latest",
            State = "Ticket text",
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is it urgent?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payments", ["technical"] = "Bugs" }
                },
                ["mood"] = new DecisionScoreQuestion
                {
                    Instructions = "How upset?",
                    Criteria = ["calm", "upset"]
                }
            }
        });

        Assert.NotNull(capturedBody);
        var payload = capturedBody!.RootElement;
        Assert.Equal("typesafe/jev-latest", payload.GetProperty("model").GetString());
        Assert.Equal("Ticket text", payload.GetProperty("state").GetString());
        Assert.Equal("noul", payload.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString());
        Assert.Equal("choice", payload.GetProperty("questions").GetProperty("team").GetProperty("type").GetString());
        Assert.Equal("score", payload.GetProperty("questions").GetProperty("mood").GetProperty("type").GetString());
        Assert.Equal("Payments", payload.GetProperty("questions").GetProperty("team").GetProperty("criteria").GetProperty("billing").GetString());
        Assert.Equal(0.95, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(result.Answers["mood"]).Score);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal(5, result.Usage.OutputTokens);
        Assert.Equal("inworld/typesafe/jev-1.13.0", result.Response!.ModelId);
        Assert.Equal("generation-1", result.Response.Id);
    }
   

    [Fact]
    public async Task Decisions_convert_sdk_images_to_native_state_parts()
    {
        var provider = CreateProvider(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var state = body.RootElement.GetProperty("state");
            Assert.Equal(JsonValueKind.Array, state.ValueKind);
            Assert.Equal("Evaluate this image.", state[0].GetString());
            Assert.Equal("image_url", state[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,aW1hZ2U=",
                state[1].GetProperty("image_url").GetProperty("url").GetString());
            return JsonResponse(
                """
                {
                  "model":"inworld/clef",
                  "answers":{"visible":{"type":"noul","noul":0.9}},
                  "usage":{"input_tokens":10,"output_tokens":1}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "inworld/inworld/clef",
            StateFormat = "sdk",
            State = JsonSerializer.SerializeToElement(new object[]
            {
                new { type = "text", text = "Evaluate this image." },
                new
                {
                    type = "file",
                    mediaType = "image/png",
                    data = new { type = "data", data = "aW1hZ2U=" }
                }
            }),
            Questions = new()
            {
                ["visible"] = new DecisionBooleanQuestion { Instructions = "Is anything visible?" }
            }
        });

        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(result.Answers["visible"]).Probability);
    }

    [Fact]
    public async Task Responses_use_native_endpoint_for_non_streaming_and_streaming_requests()
    {
        var paths = new List<string>();
        var requestBodies = new List<JsonDocument>();
        var provider = CreateProvider(async request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            requestBodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync()));
            Assert.Equal("test-model", requestBodies[^1].RootElement.GetProperty("model").GetString());
            if (requestBodies[^1].RootElement.TryGetProperty("stream", out var stream)
                && stream.GetBoolean())
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(string.Empty, Encoding.UTF8, "text/event-stream")
                };
            }

            return JsonResponse(
                """
                {
                  "id":"resp_1","object":"response","created_at":1,"status":"completed",
                  "model":"test-model","output":[],"usage":{"input_tokens":1,"output_tokens":0,"total_tokens":1}
                }
                """);
        });

        var response = await provider.ResponsesAsync(new()
        {
            Model = "inworld/test-model",
            Input = "Hello"
        });
        await foreach (var _ in provider.ResponsesStreamingAsync(new()
        {
            Model = "inworld/test-model",
            Input = "Hello"
        }))
        {
        }

        Assert.Equal(["/v1/responses", "/v1/responses"], paths);
        Assert.False(requestBodies[0].RootElement.GetProperty("stream").GetBoolean());
        Assert.True(requestBodies[1].RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("inworld/test-model", response.Model);
    }

    [Fact]
    public async Task CompleteChatAsync_rewrites_provider_shortcut_to_model_and_extra_body_provider()
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
                      "model": "openai/gpt-4o",
                      "choices": [],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "inworld/SERVICE_PROVIDER_OPENAI/gpt-4o",
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
        var root = capturedBody!.RootElement;
        Assert.Equal("gpt-4o", root.GetProperty("model").GetString());
        Assert.Equal("SERVICE_PROVIDER_OPENAI", root.GetProperty("extra_body").GetProperty("provider").GetString());
    }

    [Fact]
    public async Task CompleteChatAsync_preserves_router_shortcut_model()
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
                      "model": "inworld/my-router",
                      "choices": [],
                      "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                    }
                    """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "inworld/my-router",
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
        var root = capturedBody!.RootElement;
        Assert.Equal("inworld/my-router", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("extra_body", out _));
    }

    private static InworldProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new InworldProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient),
            new NullEndUserIdResolver());
    }

    private static InworldProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "inworld" ? "test-key" : null;
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
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
